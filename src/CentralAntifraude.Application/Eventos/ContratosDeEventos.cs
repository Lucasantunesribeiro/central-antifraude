using CentralAntifraude.Domain.Eventos;

namespace CentralAntifraude.Application.Eventos;

/// <summary>
/// Acesso a Outbox.
///
/// Nesta fase so existe a escrita, e ela nunca acontece sozinha: a linha de
/// evento entra na mesma transacao da mudanca que a originou. A leitura das
/// pendentes, o despacho em lote com <c>FOR UPDATE SKIP LOCKED</c> e a marca
/// de publicado sao a Fase 5 (CLAUDE.md secao 39).
/// </summary>
public interface IRepositorioDeEventos
{
    /// <summary>
    /// Enfileira um evento para sair.
    ///
    /// Nao grava por conta propria — quem chama decide o momento do
    /// <c>SaveChanges</c>, porque o ponto inteiro do padrao e que o evento e a
    /// mudanca sejam gravados juntos.
    /// </summary>
    void Adicionar(EventoDeSaida evento);

    /// <summary>Eventos pendentes de uma transacao, para consulta e teste.</summary>
    Task<IReadOnlyList<EventoDeSaida>> ListarPendentesAsync(
        int limite,
        CancellationToken cancellationToken);
}
