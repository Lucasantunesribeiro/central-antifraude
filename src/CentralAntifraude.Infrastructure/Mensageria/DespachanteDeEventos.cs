using System.Data;
using System.Diagnostics.Metrics;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Application.Observabilidade;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Tempo;
using CentralAntifraude.Infrastructure.Persistencia;
using CentralAntifraude.Infrastructure.Persistencia.Configuracoes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CentralAntifraude.Infrastructure.Mensageria;

/// <summary>
/// Tira da Outbox o que ainda nao saiu e publica na fila.
///
/// **A ordem das operacoes e a decisao inteira** (CLAUDE.md secao 39):
///
/// ```text
/// BEGIN
///   trava as pendentes com FOR UPDATE SKIP LOCKED
///   publica na fila
///   marca como publicado
/// COMMIT
/// ```
///
/// Publicar ANTES de marcar e deliberado. Se o processo cair entre as duas
/// coisas, a mensagem sai de novo no proximo ciclo — duplicata, que o
/// consumidor idempotente resolve. Marcar antes de publicar trocaria isso por
/// evento perdido para sempre, que ninguem resolve (CLAUDE.md secao 40).
///
/// **Varios despachantes ao mesmo tempo sao seguros.** `SKIP LOCKED` faz cada
/// um pegar um conjunto diferente: quem chega depois pula as linhas ja
/// travadas em vez de esperar por elas.
///
/// **O despachante le a Outbox de todos os tenants**, porque roda fora de uma
/// requisicao e nao tem identidade. O filtro global e desligado
/// explicitamente, pelo nome. Nada do que ele le sai do processo: o conteudo
/// vai para a fila e o consumidor confere o tenant antes de qualquer efeito.
/// </summary>
public sealed partial class DespachanteDeEventos
{
    /// <summary>Quantos eventos saem por ciclo.</summary>
    public const int TamanhoDoLote = 20;

    private static readonly Meter Medidor = new(Telemetria.MedidorDeMensageria);

    private static readonly Counter<long> Publicados =
        Medidor.CreateCounter<long>(Telemetria.Instrumentos.OutboxPublicados);

    /// <summary>
    /// Publicacoes que falharam. Sobe sem que nada quebre — o evento continua
    /// pendente e volta no proximo ciclo —, e por isso precisa de metrica: e
    /// uma falha que nao aparece em lugar nenhum como erro.
    /// </summary>
    private static readonly Counter<long> Falhas =
        Medidor.CreateCounter<long>(Telemetria.Instrumentos.OutboxFalhas);

    private readonly CentralAntifraudeDbContext _contexto;
    private readonly IFilaDeMensagens _fila;
    private readonly IRelogio _relogio;
    private readonly ILogger<DespachanteDeEventos> _log;

    public DespachanteDeEventos(
        CentralAntifraudeDbContext contexto,
        IFilaDeMensagens fila,
        IRelogio relogio,
        ILogger<DespachanteDeEventos> log)
    {
        _contexto = contexto;
        _fila = fila;
        _relogio = relogio;
        _log = log;
    }

    public async Task<ResultadoDoDespacho> DespacharLoteAsync(CancellationToken cancellationToken)
    {
        await using var transacao = await _contexto.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var pendentes = await ReivindicarPendentesAsync(cancellationToken);

        if (pendentes.Count == 0)
        {
            await transacao.CommitAsync(cancellationToken);
            return new ResultadoDoDespacho(0, 0);
        }

        var publicados = 0;
        var falhados = 0;
        var agora = _relogio.Agora;

        foreach (var evento in pendentes)
        {
            // O escopo repoe a correlacao da requisicao que originou o evento.
            // O despachante roda fora de qualquer requisicao: sem isto, o unico
            // trecho do fluxo sem `CorrelationId` no log seria justamente a
            // fronteira entre o sincrono e o assincrono — o lugar onde uma
            // investigacao mais precisa dele.
            using var escopoDeLog = _log.BeginScope(
                EscopoDeLog.DoEvento(evento.IdDeCorrelacao, evento.Id, evento.Tipo));

            try
            {
                var envelope = EnvelopeDeEvento.De(evento);
                var fila = RoteamentoDeFilas.Para(evento.Tipo);

                // A fila e escolhida pelo TIPO do evento, e nao fixa: desde a
                // Fase 9 o mesmo despachante alimenta a fila operacional e a
                // de backtests (CLAUDE.md secao 30).
                await _fila.EnviarAsync(
                    fila,
                    SerializadorDeEnvelope.Serializar(envelope),
                    cancellationToken);

                evento.MarcarPublicado(agora);
                publicados++;
                Publicados.Add(1);

                // Uma linha por evento, e nao so o resumo do ciclo. E ela que
                // fecha o vao entre "a requisicao respondeu" e "o worker agiu":
                // sem ela, seguir um `CorrelationId` no log leva ate a gravacao
                // e recomeca do outro lado, sem nada explicando a travessia.
                RegistrarPublicacao(_log, evento.Id, evento.Tipo, fila);
            }
            catch (Exception excecao) when (excecao is not OperationCanceledException)
            {
                // Falha parcial nao derruba o lote (ROADMAP 5.4). O evento
                // continua pendente, com a tentativa contada, e volta no
                // proximo ciclo. Derrubar o lote inteiro por causa de um
                // evento faria um unico registro defeituoso segurar todos os
                // outros.
                evento.RegistrarTentativaFalha();
                falhados++;
                Falhas.Add(1);

                RegistrarFalhaDePublicacao(_log, evento.Id, evento.Tipo, excecao);
            }
        }

        await _contexto.SaveChangesAsync(cancellationToken);
        await transacao.CommitAsync(cancellationToken);

        if (publicados > 0 || falhados > 0)
        {
            RegistrarCiclo(_log, publicados, falhados);
        }

        return new ResultadoDoDespacho(publicados, falhados);
    }

    /// <summary>
    /// Trava e carrega as pendentes.
    ///
    /// `FOR UPDATE SKIP LOCKED` nao tem traducao no LINQ, entao a consulta e
    /// crua. As entidades voltam rastreadas pelo EF, e por isso
    /// `MarcarPublicado` e gravado normalmente no `SaveChanges`.
    ///
    /// **O `IgnoreQueryFilters` nao e opcional, e a razao surpreende.** SQL
    /// cru NAO escapa do filtro global: o EF Core compoe o filtro POR CIMA do
    /// `FromSql`, como se fosse uma subconsulta. Como o despachante roda fora
    /// de uma requisicao e nao tem identidade, o tenant efetivo e vazio e a
    /// consulta devolveria zero linha — a Outbox pareceria sempre vazia e
    /// nenhum evento sairia nunca, sem erro nenhum no caminho.
    ///
    /// O filtro e desligado pelo nome, e nao com um `IgnoreQueryFilters()`
    /// seco: qualquer outro filtro que venha a existir continua valendo.
    /// </summary>
    private async Task<List<EventoDeSaida>> ReivindicarPendentesAsync(CancellationToken cancellationToken) =>
        await _contexto.EventosDeSaida
            .FromSql(
                $"""
                 SELECT * FROM eventos_de_saida
                 WHERE publicado_em IS NULL
                 ORDER BY ocorrido_em
                 FOR UPDATE SKIP LOCKED
                 LIMIT {TamanhoDoLote}
                 """)
            .IgnoreQueryFilters([CentralAntifraudeDbContext.FiltroDeTenant])
            .ToListAsync(cancellationToken);

    [LoggerMessage(
        EventId = 500,
        Level = LogLevel.Information,
        Message = "Despacho da Outbox: {Publicados} publicado(s), {Falhados} com falha.")]
    private static partial void RegistrarCiclo(ILogger logger, int publicados, int falhados);

    [LoggerMessage(
        EventId = 502,
        Level = LogLevel.Information,
        Message = "Evento {EventoId} do tipo {Tipo} publicado na fila {Fila}.")]
    private static partial void RegistrarPublicacao(
        ILogger logger,
        Guid eventoId,
        string tipo,
        string fila);

    // O conteudo do evento nao entra no log: ele carrega score, decisao e
    // identificadores de cliente (CLAUDE.md secao 70). O identificador e o
    // tipo bastam para investigar.
    [LoggerMessage(
        EventId = 501,
        Level = LogLevel.Warning,
        Message = "Falha ao publicar o evento {EventoId} do tipo {Tipo}. Continua pendente.")]
    private static partial void RegistrarFalhaDePublicacao(
        ILogger logger,
        Guid eventoId,
        string tipo,
        Exception excecao);
}
