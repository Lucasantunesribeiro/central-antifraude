using CentralAntifraude.Application.Backtests;
using CentralAntifraude.Application.Correlacao;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Application.Risco;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Infrastructure.Mensageria;
using CentralAntifraude.Infrastructure.Persistencia;
using CentralAntifraude.Infrastructure.Persistencia.Repositorios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CentralAntifraude.IntegrationTests.Infra;

/// <summary>
/// Monta despachante, fila e worker fora do host da API.
///
/// Os lacos de fundo ficam desligados nos testes de proposito: um laco
/// rodando sozinho tornaria "quantas mensagens sobraram na fila" uma pergunta
/// sem resposta estavel, e o resultado passaria a depender de quem ganhou a
/// corrida. Aqui cada ciclo e chamado explicitamente, e o que o teste afirma e
/// o que de fato aconteceu.
/// </summary>
internal sealed class CenarioDeMensageria : IAsyncDisposable
{
    private readonly CentralAntifraudeDbContext _contexto;

    private CenarioDeMensageria(
        CentralAntifraudeDbContext contexto,
        IFilaDeMensagens fila,
        DespachanteDeEventos despachante,
        ProcessadorDeEventos processador,
        ProcessadorDeBacktests backtests,
        OpcoesDaFila opcoes)
    {
        _contexto = contexto;
        Fila = fila;
        Despachante = despachante;
        Processador = processador;
        Backtests = backtests;
        Opcoes = opcoes;
    }

    public IFilaDeMensagens Fila { get; }

    public DespachanteDeEventos Despachante { get; }

    public ProcessadorDeEventos Processador { get; }

    /// <summary>O consumidor da fila dedicada de backtests (Fase 9).</summary>
    public ProcessadorDeBacktests Backtests { get; }

    public OpcoesDaFila Opcoes { get; }

    /// <summary>
    /// Cria o cenario com um contexto proprio, sem identidade.
    ///
    /// Sem identidade e o certo: despachante e worker rodam fora de uma
    /// requisicao. Se algum deles dependesse do filtro global de tenant para
    /// enxergar o que precisa, ele veria vazio aqui — e o teste denunciaria.
    /// </summary>
    /// <param name="envolverFila">
    /// Substitui a fila por uma versao decorada. Existe para os testes de
    /// resiliencia: e o unico jeito de simular "o broker esta fora" sem
    /// derrubar o banco junto, que e o que aconteceria se a fila fosse
    /// desligada por baixo.
    /// </param>
    /// <param name="manipuladores">
    /// Substitui os efeitos. Um efeito que falha e um cenario legitimo — banco
    /// intermitente, conflito — e o worker precisa se comportar do mesmo jeito
    /// nos dois casos.
    /// </param>
    public static CenarioDeMensageria Criar(
        string stringDeConexao,
        OpcoesDaFila? opcoes = null,
        OpcoesDeBacktest? opcoesDeBacktest = null,
        Func<IFilaDeMensagens, IFilaDeMensagens>? envolverFila = null,
        IReadOnlyList<IManipuladorDeEvento>? manipuladores = null)
    {
        var contexto = CenarioDeIdentidade.CriarContexto(stringDeConexao);
        var opcoesDaFila = opcoes ?? new OpcoesDaFila();
        var relogio = new RelogioDeTeste();

        IFilaDeMensagens fila = new FilaEmPostgres(contexto, opcoesDaFila, NullLogger<FilaEmPostgres>.Instance);

        fila = envolverFila is null ? fila : envolverFila(fila);

        var despachante = new DespachanteDeEventos(
            contexto,
            fila,
            relogio,
            NullLogger<DespachanteDeEventos>.Instance);

        // Os efeitos, na mesma ordem em que a composicao real os registra. O
        // teste precisa da mesma ordem porque ela decide qual savepoint cobre
        // qual efeito.
        IReadOnlyList<IManipuladorDeEvento> efeitos = manipuladores ??
        [
            new ProjecaoDeDecisoesDiarias(contexto),
            new CriadorDeAlertas(
                new RepositorioDeAlertas(contexto),
                relogio,
                NullLogger<CriadorDeAlertas>.Instance),
        ];

        var processador = new ProcessadorDeEventos(
            contexto,
            new UnidadeDeTrabalho(contexto),
            fila,
            efeitos,
            opcoesDaFila,
            relogio,
            new ContextoDeCorrelacaoMutavel(),
            NullLogger<ProcessadorDeEventos>.Instance);

        // O consumidor de backtests roda com o MESMO motor da avaliacao real:
        // e isso que o ROADMAP 9.4 exige, e montar aqui um motor diferente
        // faria o teste provar o contrario do que ele afirma.
        var opcoesDoBacktest = opcoesDeBacktest ?? new OpcoesDeBacktest();
        var repositorioDeBacktests = new RepositorioDeBacktests(contexto);

        var backtests = new ProcessadorDeBacktests(
            repositorioDeBacktests,
            new ExecutorDeBacktest(
                repositorioDeBacktests,
                new MotorDeRisco(),
                new OpcoesDeAvaliacao(),
                opcoesDoBacktest,
                relogio),
            new UnidadeDeTrabalho(contexto),
            fila,
            opcoesDoBacktest,
            relogio,
            new ContextoDeCorrelacaoMutavel(),
            NullLogger<ProcessadorDeBacktests>.Instance);

        return new CenarioDeMensageria(
            contexto,
            fila,
            despachante,
            processador,
            backtests,
            opcoesDaFila);
    }

    /// <summary>
    /// Zera o estado global da mensageria antes de um teste.
    ///
    /// **Isto existe por causa de uma propriedade real do desenho, e nao de um
    /// defeito.** O despachante le a Outbox de TODOS os tenants — ele roda
    /// fora de uma requisicao e nao tem identidade. As outras classes de teste
    /// ingerem transacoes e nunca despacham, entao deixam eventos pendentes
    /// para tras.
    ///
    /// Sem esta limpeza, um teste de mensageria publicaria o acumulo das
    /// outras classes junto com o proprio evento, e "quantas mensagens tem na
    /// fila" deixaria de ter resposta estavel. Marcar o acumulo como publicado
    /// e mais honesto do que apaga-lo: o evento aconteceu mesmo, e so nao
    /// interessa a este teste.
    /// </summary>
    public static async Task LimparAsync(string stringDeConexao, CancellationToken cancellationToken)
    {
        await using var contexto = CenarioDeIdentidade.CriarContexto(stringDeConexao);

        await contexto.Database.ExecuteSqlRawAsync(
            """
            UPDATE eventos_de_saida SET publicado_em = now() WHERE publicado_em IS NULL;
            DELETE FROM fila_de_mensagens;
            DELETE FROM mensagens_mortas;
            """,
            cancellationToken);
    }

    /// <summary>Despacha e consome ate a fila esvaziar ou o limite acabar.</summary>
    public async Task<int> RodarAteEsvaziarAsync(CancellationToken cancellationToken, int maximoDeCiclos = 10)
    {
        var ciclos = 0;

        for (; ciclos < maximoDeCiclos; ciclos++)
        {
            var despacho = await Despachante.DespacharLoteAsync(cancellationToken);
            var consumo = await Processador.ConsumirLoteAsync(cancellationToken);

            if (despacho.Total == 0 && consumo.Total == 0)
            {
                break;
            }
        }

        return ciclos;
    }

    /// <summary>
    /// Despacha a Outbox e consome a fila de backtests ate ela esvaziar.
    ///
    /// Separado do ciclo operacional de proposito: e assim que o teste
    /// consegue afirmar "o worker de alertas nao encostou nesta mensagem".
    /// </summary>
    public async Task<int> RodarBacktestsAteEsvaziarAsync(
        CancellationToken cancellationToken,
        int maximoDeCiclos = 10)
    {
        var ciclos = 0;

        for (; ciclos < maximoDeCiclos; ciclos++)
        {
            var despacho = await Despachante.DespacharLoteAsync(cancellationToken);
            var consumo = await Backtests.ConsumirLoteAsync(cancellationToken);

            if (despacho.Total == 0 && consumo.Total == 0)
            {
                break;
            }
        }

        return ciclos;
    }

    public ValueTask DisposeAsync() => _contexto.DisposeAsync();

    /// <summary>Relogio real; nenhum teste desta area depende de congelar o tempo.</summary>
    private sealed class RelogioDeTeste : Domain.Tempo.IRelogio
    {
        public DateTimeOffset Agora => Domain.Tempo.Instante.Normalizar(DateTimeOffset.UtcNow);
    }
}
