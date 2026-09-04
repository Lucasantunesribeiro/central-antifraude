using CentralAntifraude.Application.Eventos;
using CentralAntifraude.Domain.Eventos;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.Infrastructure.Persistencia.Repositorios;

/// <summary>
/// Escrita e leitura da Outbox.
///
/// <c>Adicionar</c> nao grava: quem chama decide o momento do
/// <c>SaveChanges</c>, porque o ponto inteiro do padrao e que o evento e a
/// mudanca que o originou sejam gravados na mesma transacao.
/// </summary>
public sealed class RepositorioDeEventos : IRepositorioDeEventos
{
    private readonly CentralAntifraudeDbContext _contexto;

    public RepositorioDeEventos(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    public void Adicionar(EventoDeSaida evento)
    {
        ArgumentNullException.ThrowIfNull(evento);

        _contexto.EventosDeSaida.Add(evento);
    }

    public async Task<IReadOnlyList<EventoDeSaida>> ListarPendentesAsync(
        int limite,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limite);

        // Mais antigos primeiro. Nao e garantia de ordem de entrega — o
        // CLAUDE.md secao 43 proibe presumir ordem global —, e apenas justica:
        // um evento antigo nao deve ficar para tras porque chegaram novos.
        return await _contexto.EventosDeSaida
            .AsNoTracking()
            .Where(e => e.PublicadoEm == null)
            .OrderBy(e => e.OcorridoEm)
            .ThenBy(e => e.Id)
            .Take(limite)
            .ToListAsync(cancellationToken);
    }
}
