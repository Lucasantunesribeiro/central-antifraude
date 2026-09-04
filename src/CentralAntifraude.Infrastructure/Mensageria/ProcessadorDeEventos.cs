using System.Data;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Tempo;
using CentralAntifraude.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace CentralAntifraude.Infrastructure.Mensageria;

/// <summary>
/// O consumidor: le a fila, aplica o efeito uma unica vez e confirma.
///
/// **A ordem importa e e esta:**
///
/// ```text
/// recebe (mensagem fica escondida)
///   BEGIN
///     grava a Inbox        ← a restricao unica e quem decide
///     aplica o efeito
///   COMMIT
/// apaga da fila
/// ```
///
/// Inbox e efeito no MESMO commit. Se fossem separados existiria o instante em
/// que o efeito aconteceu e a marca nao — e a reentrega repetiria o efeito,
/// que e exatamente o que a Inbox deveria impedir.
///
/// A mensagem so e apagada DEPOIS do commit. Se o processo cair entre uma
/// coisa e outra, a mensagem volta, a Inbox reconhece e o efeito nao acontece
/// de novo. O contrario — apagar antes — trocaria duplicata por perda.
///
/// **O que este worker nunca faz: confiar no que chega.** O envelope e dado,
/// nao instrucao. O tenant declarado e conferido contra o banco, o tipo e
/// conferido contra a lista fechada, e o que nao passa vai para o caminho de
/// falha em vez de virar efeito.
/// </summary>
public sealed partial class ProcessadorDeEventos
{
    /// <summary>Nome deste consumidor na Inbox. Faz parte da chave de unicidade.</summary>
    public const string Consumidor = "resumo-diario-de-decisoes";

    private readonly CentralAntifraudeDbContext _contexto;
    private readonly IUnidadeDeTrabalho _unidadeDeTrabalho;
    private readonly IFilaDeMensagens _fila;
    private readonly OpcoesDaFila _opcoes;
    private readonly IRelogio _relogio;
    private readonly ILogger<ProcessadorDeEventos> _log;

    public ProcessadorDeEventos(
        CentralAntifraudeDbContext contexto,
        IUnidadeDeTrabalho unidadeDeTrabalho,
        IFilaDeMensagens fila,
        OpcoesDaFila opcoes,
        IRelogio relogio,
        ILogger<ProcessadorDeEventos> log)
    {
        _contexto = contexto;
        _unidadeDeTrabalho = unidadeDeTrabalho;
        _fila = fila;
        _opcoes = opcoes;
        _relogio = relogio;
        _log = log;
    }

    public async Task<ResultadoDoConsumo> ConsumirLoteAsync(CancellationToken cancellationToken)
    {
        var mensagens = await _fila.ReceberAsync(
            IFilaDeMensagens.FilaOperacional,
            _opcoes.TamanhoDoLote,
            cancellationToken);

        var processados = 0;
        var repetidos = 0;
        var recusados = 0;
        var falhados = 0;

        foreach (var mensagem in mensagens)
        {
            // Falha parcial nao contamina o lote (ROADMAP 5.8). Cada mensagem
            // tem seu proprio destino: apagada, devolvida ou deixada para a
            // visibilidade expirar. Uma mensagem ruim no meio de nove boas nao
            // pode obrigar as nove a serem reprocessadas.
            switch (await TratarAsync(mensagem, cancellationToken))
            {
                case Desfecho.Aplicado:
                    processados++;
                    break;
                case Desfecho.JaProcessado:
                    repetidos++;
                    break;
                case Desfecho.Recusado:
                    recusados++;
                    break;
                default:
                    falhados++;
                    break;
            }
        }

        if (mensagens.Count > 0)
        {
            RegistrarCiclo(_log, processados, repetidos, recusados, falhados);
        }

        return new ResultadoDoConsumo(processados, repetidos, recusados, falhados);
    }

    private async Task<Desfecho> TratarAsync(
        MensagemRecebida mensagem,
        CancellationToken cancellationToken)
    {
        EnvelopeDeEvento envelope;

        try
        {
            envelope = SerializadorDeEnvelope.Desserializar(mensagem.Corpo);
        }
        catch (EnvelopeInvalido excecao)
        {
            RegistrarMensagemInvalida(_log, mensagem.RecebimentosAproximados, excecao.Message);

            return await DevolverParaRedriveAsync(mensagem, cancellationToken);
        }

        // O tenant do envelope e uma AFIRMACAO de quem enviou. Conferir contra
        // o banco e o que separa "evento nosso" de "mensagem forjada": uma
        // mensagem que declare o tenant A e aponte para uma transacao do
        // tenant B produziria um numero no painel do cliente errado.
        if (envelope.Conteudo is not TransacaoAvaliadaV1 conteudo ||
            !await TenantConfereAsync(envelope.TenantId, conteudo.TransacaoId, cancellationToken))
        {
            RegistrarTenantInconsistente(_log, envelope.EventId, mensagem.RecebimentosAproximados);

            return await DevolverParaRedriveAsync(mensagem, cancellationToken);
        }

        try
        {
            var desfecho = await AplicarAsync(envelope, conteudo, cancellationToken);

            // Confirmacao SO depois do commit.
            await _fila.ApagarAsync(mensagem.Recibo, cancellationToken);

            // O fio da correlacao chega ate aqui (CLAUDE.md secao 69). E este
            // registro que permite pegar o identificador que o integrador
            // recebeu na resposta HTTP e encontrar o efeito que aconteceu
            // segundos depois, em outro processo.
            var nomeDoDesfecho = desfecho.ToString();
            RegistrarEfeito(_log, envelope.EventId, envelope.CorrelationId, nomeDoDesfecho);

            return desfecho;
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            // Falha que pode ser transitoria — banco indisponivel, conflito de
            // concorrencia. A mensagem NAO e apagada: a visibilidade expira e
            // ela volta. Se for permanente, o redrive a tira de circulacao
            // depois do numero configurado de entregas.
            RegistrarFalhaNoEfeito(_log, envelope.EventId, excecao);

            return Desfecho.Falhou;
        }
    }

    /// <summary>
    /// Grava a Inbox e aplica o efeito, na mesma transacao.
    ///
    /// A restricao unica de (consumidor, evento) e quem decide se o efeito
    /// acontece. Nao ha consulta previa: consultar e depois inserir seria a
    /// mesma armadilha do <c>SELECT</c> seguido de <c>INSERT</c> que a
    /// ingestao ja evita — dois workers concorrentes passariam os dois pela
    /// consulta e somariam duas vezes.
    /// </summary>
    private async Task<Desfecho> AplicarAsync(
        EnvelopeDeEvento envelope,
        TransacaoAvaliadaV1 conteudo,
        CancellationToken cancellationToken)
    {
        await using var transacao = await _contexto.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        _contexto.EventosProcessados.Add(EventoProcessado.Registrar(
            envelope.TenantId,
            envelope.EventId,
            Consumidor,
            envelope.TipoComposto,
            envelope.CorrelationId,
            _relogio.Agora));

        try
        {
            // Pela unidade de trabalho, e nao pelo DbContext direto: e ela que
            // traduz a violacao de unicidade do PostgreSQL em uma excecao que
            // este codigo consegue distinguir de qualquer outra falha de
            // gravacao.
            await _unidadeDeTrabalho.SalvarAsync(cancellationToken);
        }
        catch (ConflitoDeUnicidadeNoBanco)
        {
            // Ja tratado. E o caminho normal de uma reentrega, nao um erro:
            // acontece toda vez que o processo cai entre o commit e a
            // confirmacao na fila.
            await transacao.RollbackAsync(cancellationToken);
            _contexto.ChangeTracker.Clear();

            return Desfecho.JaProcessado;
        }

        await SomarNoResumoAsync(envelope, conteudo, cancellationToken);

        await transacao.CommitAsync(cancellationToken);

        return Desfecho.Aplicado;
    }

    /// <summary>
    /// Soma um na contagem do dia.
    ///
    /// `ON CONFLICT ... DO UPDATE` porque a linha pode nao existir ainda e
    /// dois workers podem cria-la ao mesmo tempo. Ler, somar e gravar em
    /// passos separados perderia incrementos sob concorrencia — e um contador
    /// que perde nao deixa rastro nenhum.
    ///
    /// O dia e o da AVALIACAO, em UTC: uma transacao atrasada de tres dias
    /// atras foi decidida hoje, e e hoje que ela entrou na fila do analista.
    /// </summary>
    private async Task SomarNoResumoAsync(
        EnvelopeDeEvento envelope,
        TransacaoAvaliadaV1 conteudo,
        CancellationToken cancellationToken)
    {
        var conexao = (NpgsqlConnection)_contexto.Database.GetDbConnection();

        await using var comando = conexao.CreateCommand();
        comando.Transaction =
            _contexto.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;

        comando.CommandText =
            """
            INSERT INTO resumo_diario_de_decisoes
                (id, organizacao_id, dia, decisao, quantidade, atualizado_em)
            VALUES (gen_random_uuid(), @organizacao, @dia, @decisao, 1, now())
            ON CONFLICT (organizacao_id, dia, decisao)
            DO UPDATE SET quantidade = resumo_diario_de_decisoes.quantidade + 1,
                          atualizado_em = now()
            """;

        comando.Parameters.Add(new NpgsqlParameter("organizacao", NpgsqlDbType.Uuid)
        {
            Value = envelope.TenantId,
        });
        comando.Parameters.Add(new NpgsqlParameter("dia", NpgsqlDbType.Date)
        {
            Value = DateOnly.FromDateTime(conteudo.AvaliadaEm.UtcDateTime),
        });
        comando.Parameters.Add(new NpgsqlParameter("decisao", NpgsqlDbType.Text)
        {
            Value = conteudo.Decisao.ToString(),
        });

        await comando.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// A transacao citada existe e pertence ao tenant que o envelope declara?
    ///
    /// Ignora o filtro global de proposito: o worker roda sem identidade, e
    /// aqui a pergunta e exatamente "de quem e esta transacao?". Confiar no
    /// filtro devolveria vazio e a resposta viraria "nao existe" para tudo.
    /// </summary>
    private async Task<bool> TenantConfereAsync(
        Guid tenant,
        Guid transacaoId,
        CancellationToken cancellationToken)
    {
        var organizacao = await _contexto.Transacoes
            .IgnoreQueryFilters([CentralAntifraudeDbContext.FiltroDeTenant])
            .Where(t => t.Id == transacaoId)
            .Select(t => (Guid?)t.OrganizacaoId)
            .FirstOrDefaultAsync(cancellationToken);

        return organizacao == tenant;
    }

    /// <summary>
    /// Devolve a mensagem sem apagar, para que a politica de redrive decida.
    ///
    /// **Nao se apaga o que nao se entende.** Apagar em silencio esconderia o
    /// problema; e a DLQ que existe para guardar a mensagem para analise
    /// humana. Devolver na hora — em vez de esperar a visibilidade expirar —
    /// so acelera o caminho ate la, do mesmo jeito que um
    /// <c>ChangeMessageVisibility(0)</c> faria no SQS.
    /// </summary>
    private async Task<Desfecho> DevolverParaRedriveAsync(
        MensagemRecebida mensagem,
        CancellationToken cancellationToken)
    {
        await _fila.DevolverAsync(mensagem.Recibo, cancellationToken);

        return Desfecho.Recusado;
    }

    private enum Desfecho
    {
        Aplicado,
        JaProcessado,
        Recusado,
        Falhou,
    }

    [LoggerMessage(
        EventId = 510,
        Level = LogLevel.Information,
        Message = "Consumo: {Processados} aplicado(s), {Repetidos} repetido(s), " +
                  "{Recusados} recusado(s), {Falhados} com falha.")]
    private static partial void RegistrarCiclo(
        ILogger logger,
        int processados,
        int repetidos,
        int recusados,
        int falhados);

    // O corpo da mensagem NAO entra no log: ele carrega score, decisao e
    // identificador de cliente. O motivo e a contagem de entregas bastam.
    [LoggerMessage(
        EventId = 511,
        Level = LogLevel.Warning,
        Message = "Mensagem invalida na entrega {Recebimentos}: {Motivo}. Devolvida para redrive.")]
    private static partial void RegistrarMensagemInvalida(
        ILogger logger,
        int recebimentos,
        string motivo);

    [LoggerMessage(
        EventId = 512,
        Level = LogLevel.Warning,
        Message = "Evento {EventoId} declara um tenant que nao confere com o dado. " +
                  "Entrega {Recebimentos}. Devolvido para redrive.")]
    private static partial void RegistrarTenantInconsistente(
        ILogger logger,
        Guid eventoId,
        int recebimentos);

    // O identificador de correlacao entra; o conteudo do evento, nao. A
    // correlacao e um numero de protocolo, e nao dado do cliente.
    [LoggerMessage(
        EventId = 514,
        Level = LogLevel.Information,
        Message = "Evento {EventoId} tratado com desfecho {Desfecho}. Correlacao {IdDeCorrelacao}.")]
    private static partial void RegistrarEfeito(
        ILogger logger,
        Guid eventoId,
        string idDeCorrelacao,
        string desfecho);

    [LoggerMessage(
        EventId = 513,
        Level = LogLevel.Error,
        Message = "Falha ao aplicar o efeito do evento {EventoId}. A mensagem volta para a fila.")]
    private static partial void RegistrarFalhaNoEfeito(
        ILogger logger,
        Guid eventoId,
        Exception excecao);
}
