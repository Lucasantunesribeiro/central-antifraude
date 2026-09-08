using System.Data;
using System.Diagnostics.Metrics;
using System.Globalization;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Observabilidade;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CentralAntifraude.Infrastructure.Persistencia;

/// <summary>
/// O boundary transacional forte da ingestao, com retry deliberado.
///
/// **Por que `SERIALIZABLE` e nao `READ COMMITTED` com trava.** A avaliacao le
/// um conjunto — "quantas transacoes deste cliente nos ultimos 10 minutos" — e
/// decide a partir dele. Nao ha uma linha unica para travar: o risco e uma
/// leitura que deixa de ser verdadeira porque alguem INSERIU algo que ela
/// deveria ter visto. `SERIALIZABLE` no PostgreSQL detecta exatamente isso e
/// aborta uma das transacoes com SQLSTATE 40001 (CLAUDE.md secao 35).
///
/// A alternativa seria travar o cliente com um advisory lock. Funcionaria, mas
/// serializaria por cliente mesmo quando nao ha conflito nenhum, e trocaria
/// uma deteccao que o banco ja faz por uma trava que o codigo precisa lembrar
/// de pegar em todo caminho novo.
///
/// **O retry e da operacao inteira** (CLAUDE.md secao 36). Nao se reexecuta o
/// comando SQL que falhou: a transacao inteira e abortada, o rastreamento e
/// limpo, e a operacao roda de novo do zero — releitura do contexto e nova
/// avaliacao. Repetir so o comando gravaria uma decisao calculada sobre um
/// passado que o banco ja declarou invalido.
/// </summary>
public sealed partial class ExecutorDeOperacaoCritica : IExecutorDeOperacaoCritica
{
    /// <summary>Falha de serializacao: o banco nao conseguiu ordenar as transacoes.</summary>
    private const string FalhaDeSerializacao = "40001";

    /// <summary>Deadlock detectado. Mesma resposta: abortar e refazer.</summary>
    private const string DeadlockDetectado = "40P01";

    /// <summary>Nome do medidor. O catalogo esta em <see cref="Telemetria"/>.</summary>
    public const string NomeDoMedidor = Telemetria.MedidorDeConcorrencia;

    private static readonly Meter Medidor = new(NomeDoMedidor);

    /// <summary>
    /// Quantas vezes uma operacao critica precisou ser refeita.
    ///
    /// Sem dimensao de tenant, transacao ou correlacao: seriam dimensoes de
    /// alta cardinalidade, e o CLAUDE.md secao 71 proibe exatamente isso. O
    /// numero total ja responde a pergunta que importa — o isolamento forte
    /// esta custando caro?
    /// </summary>
    private static readonly Counter<long> Retentativas =
        Medidor.CreateCounter<long>(Telemetria.Instrumentos.Retentativas);

    /// <summary>Operacoes que esgotaram as tentativas e falharam.</summary>
    private static readonly Counter<long> Esgotadas =
        Medidor.CreateCounter<long>(Telemetria.Instrumentos.TentativasEsgotadas);

    private readonly CentralAntifraudeDbContext _contexto;
    private readonly OpcoesDeConcorrencia _opcoes;
    private readonly ILogger<ExecutorDeOperacaoCritica> _log;

    public ExecutorDeOperacaoCritica(
        CentralAntifraudeDbContext contexto,
        OpcoesDeConcorrencia opcoes,
        ILogger<ExecutorDeOperacaoCritica> log)
    {
        _contexto = contexto;
        _opcoes = opcoes;
        _log = log;
    }

    public async Task<T> ExecutarAsync<T>(
        Func<CancellationToken, Task<T>> operacao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operacao);

        for (var tentativa = 1; ; tentativa++)
        {
            var ultima = tentativa >= _opcoes.MaximoDeTentativas;

            try
            {
                return await TentarAsync(operacao, cancellationToken);
            }
            catch (Exception excecao) when (EhConflitoDeConcorrencia(excecao))
            {
                // Calculado uma vez, fora da chamada de log: o motivo entra
                // nos dois caminhos e o analisador recusa invocacao dentro do
                // argumento de LoggerMessage.
                var motivo = Motivo(excecao);

                if (ultima)
                {
                    Esgotadas.Add(1);
                    RegistrarEsgotamento(_log, tentativa, motivo);

                    // 503 com Retry-After, e nao 500. Esgotar as tentativas
                    // nao significa que algo quebrou: significa que a disputa
                    // durou mais do que o orcamento. Repetir com a mesma chave
                    // de idempotencia e seguro e e a acao correta.
                    throw new ContencaoDeConcorrencia(excecao);
                }

                Retentativas.Add(1);
                RegistrarRetentativa(_log, tentativa, motivo);

                // O rastreamento precisa ir junto. As entidades da tentativa
                // abortada continuariam marcadas como Added, e a releitura
                // devolveria instancias que nunca foram gravadas.
                _contexto.ChangeTracker.Clear();

                await Task.Delay(EsperaDe(tentativa), cancellationToken);
            }
        }
    }

    private async Task<T> TentarAsync<T>(
        Func<CancellationToken, Task<T>> operacao,
        CancellationToken cancellationToken)
    {
        await using var transacao = await _contexto.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        await EncorajarIndicesAsync(cancellationToken);

        var resultado = await operacao(cancellationToken);

        // No PostgreSQL, `SERIALIZABLE` pode falhar no COMMIT, e nao antes: e
        // ali que o banco conclui que nao existe ordem serial que explique as
        // leituras. Por isso o commit fica DENTRO do bloco protegido pelo
        // retry, e nao depois dele.
        await transacao.CommitAsync(cancellationToken);

        return resultado;
    }

    /// <summary>
    /// Torna a varredura sequencial cara aos olhos do planejador, so aqui
    /// dentro.
    ///
    /// Sem isto, a consulta de contexto varre `transacoes` inteira quando a
    /// tabela e pequena, e a varredura sequencial obriga o PostgreSQL a
    /// bloquear a RELACAO inteira como predicado. O resultado medido: doze
    /// requisicoes de doze clientes diferentes, que nao compartilham dado
    /// nenhum, produziram uma tempestade de 40001 e duas delas esgotaram as
    /// tentativas.
    ///
    /// `SET LOCAL` vale ate o fim desta transacao e nao vaza para nenhuma
    /// outra consulta do sistema. E a recomendacao da propria documentacao do
    /// PostgreSQL, aplicada no escopo mais estreito possivel.
    /// </summary>
    private async Task<int> EncorajarIndicesAsync(CancellationToken cancellationToken)
    {
        // `SET LOCAL` nao aceita parametro, entao o valor precisa ir literal
        // no comando. Ele nunca vem de entrada externa: e uma opcao de
        // configuracao do servidor, validada na inicializacao dentro de uma
        // faixa numerica fechada, e formatada com cultura invariante. E por
        // isso que a supressao abaixo e segura - e nao porque "aqui nao tem
        // usuario".
#pragma warning disable EF1002, EF1003
        return await _contexto.Database.ExecuteSqlRawAsync(
            "SET LOCAL cpu_tuple_cost = " +
            _opcoes.CustoPorLinha.ToString("0.####", CultureInfo.InvariantCulture),
            cancellationToken);
#pragma warning restore EF1002, EF1003
    }

    /// <summary>
    /// O que vale refazer, e o que nao vale.
    ///
    /// Refazer:
    /// - **40001 / 40P01** — o banco declarou que as transacoes concorrentes
    ///   nao tem ordem serial valida. Uma delas precisa recomecar.
    /// - **violacao de unicidade** — outra requisicao inseriu a mesma chave e
    ///   venceu a corrida. Detalhe que so aparece sob isolamento forte: nao
    ///   adianta reconsultar DENTRO da mesma transacao, porque o snapshot dela
    ///   e anterior ao commit da vencedora e a linha e invisivel. A terceira
    ///   camada de idempotencia passa a ser esta: refazer a operacao com um
    ///   snapshot novo, no qual a consulta inicial encontra a vencedora.
    ///
    /// Nao refazer: validacao, conflito de negocio, autenticacao, autorizacao,
    /// payload invalido, falha deterministica de regra (CLAUDE.md secao 37).
    /// Repeti-los daria o mesmo resultado, so que quatro vezes mais devagar.
    /// </summary>
    private static bool EhConflitoDeConcorrencia(Exception excecao) =>
        excecao switch
        {
            ConflitoDeUnicidadeNoBanco => true,
            PostgresException { SqlState: FalhaDeSerializacao or DeadlockDetectado } => true,
            _ => excecao.InnerException is not null && EhConflitoDeConcorrencia(excecao.InnerException),
        };

    private static string Motivo(Exception excecao) =>
        excecao switch
        {
            ConflitoDeUnicidadeNoBanco conflito => $"unicidade:{conflito.Restricao}",
            PostgresException postgres => $"sqlstate:{postgres.SqlState}",
            _ => excecao.InnerException is null
                ? excecao.GetType().Name
                : Motivo(excecao.InnerException),
        };

    /// <summary>
    /// Espera crescente com sorteio.
    ///
    /// O sorteio importa mais que o crescimento: duas requisicoes que
    /// esperassem exatamente o mesmo tempo voltariam a colidir na mesma
    /// janela, e a disputa se repetiria ate esgotar as tentativas.
    /// </summary>
    private TimeSpan EsperaDe(int tentativa)
    {
        var teto = Math.Min(_opcoes.EsperaBaseEmMs * (1 << tentativa), _opcoes.EsperaMaximaEmMs);

        return TimeSpan.FromMilliseconds(Random.Shared.Next(_opcoes.EsperaBaseEmMs, Math.Max(teto, 1) + 1));
    }

    // O motivo entra como propriedade estruturada e nunca carrega payload,
    // credencial ou identificador de cliente (CLAUDE.md secao 70).
    [LoggerMessage(
        EventId = 400,
        Level = LogLevel.Information,
        Message = "Operacao critica refeita apos conflito de concorrencia. Tentativa {Tentativa}, motivo {Motivo}.")]
    private static partial void RegistrarRetentativa(ILogger logger, int tentativa, string motivo);

    [LoggerMessage(
        EventId = 401,
        Level = LogLevel.Warning,
        Message = "Operacao critica esgotou as tentativas. Tentativas {Tentativa}, motivo {Motivo}.")]
    private static partial void RegistrarEsgotamento(ILogger logger, int tentativa, string motivo);
}
