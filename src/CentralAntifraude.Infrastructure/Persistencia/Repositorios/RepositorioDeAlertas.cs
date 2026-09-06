using CentralAntifraude.Application.Alertas;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Domain.Alertas;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.Infrastructure.Persistencia.Repositorios;

/// <summary>
/// A fila operacional, lida do banco.
///
/// **Todo filtro e toda ordenacao acontecem aqui, no servidor** (ROADMAP 6.5).
/// Trazer a fila inteira e filtrar na tela funcionaria com trinta alertas e
/// quebraria com trinta mil — e, pior, entregaria ao navegador alertas que o
/// analista pediu para nao ver, que e vazamento de dado sem necessidade.
/// </summary>
public sealed class RepositorioDeAlertas : IRepositorioDeAlertas
{
    private readonly CentralAntifraudeDbContext _contexto;

    public RepositorioDeAlertas(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    public async Task<Pagina<Alerta>> ListarAsync(
        FiltroDeAlertas filtro,
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        ArgumentNullException.ThrowIfNull(paginacao);
        ArgumentNullException.ThrowIfNull(ordenacao);

        // O tenant nao aparece em nenhum `Where` daqui de proposito: quem o
        // aplica e o filtro global do DbContext. Repeti-lo aqui daria a
        // impressao de que a protecao depende de alguem lembrar de escreve-lo.
        var consulta = _contexto.Alertas.AsNoTracking();

        if (filtro.Decisao is { } decisao)
        {
            consulta = consulta.Where(a => a.Decisao == decisao);
        }

        if (filtro.Prioridade is { } prioridade)
        {
            consulta = consulta.Where(a => a.Prioridade == prioridade);
        }

        if (filtro.Status is { } status)
        {
            consulta = consulta.Where(a => a.Status == status);
        }

        if (filtro.ScoreMinimo is { } scoreMinimo)
        {
            consulta = consulta.Where(a => a.Score >= scoreMinimo);
        }

        if (filtro.De is { } de)
        {
            consulta = consulta.Where(a => a.CriadoEm >= de);
        }

        if (filtro.Ate is { } ate)
        {
            consulta = consulta.Where(a => a.CriadoEm <= ate);
        }

        // O total respeita os mesmos filtros: uma tela que diz "1.240 alertas"
        // e mostra 12 depois de filtrar estaria mentindo sobre o tamanho do
        // trabalho.
        var total = await consulta.LongCountAsync(cancellationToken);

        var ascendente = ordenacao.Direcao == DirecaoDeOrdenacao.Ascendente;

        IOrderedQueryable<Alerta> ordenada = ordenacao.Campo switch
        {
            "score" => ascendente
                ? consulta.OrderBy(a => a.Score)
                : consulta.OrderByDescending(a => a.Score),
            // **A prioridade e gravada como texto, e texto ordena em ordem
            // alfabetica — que aqui e o INVERSO da gravidade**: "Alta" vem
            // antes de "Media" no alfabeto. Ordenar pela coluna direto poria
            // os alertas menos graves no topo quando o analista pedisse "mais
            // grave primeiro", e nada na tela denunciaria o engano.
            //
            // Por isso a ordem vem de uma expressao explicita de gravidade, e
            // nao da coluna. O texto fica no banco porque uma consulta manual
            // durante uma investigacao precisa dizer "Alta", e nao "2".
            "prioridade" => ascendente
                ? consulta.OrderBy(a => a.Prioridade == PrioridadeDeAlerta.Alta ? 1 : 0)
                : consulta.OrderByDescending(a => a.Prioridade == PrioridadeDeAlerta.Alta ? 1 : 0),
            _ => ascendente
                ? consulta.OrderBy(a => a.CriadoEm)
                : consulta.OrderByDescending(a => a.CriadoEm),
        };

        var itens = await ordenada
            // Desempate estavel. Sem ele, dois alertas criados no mesmo
            // instante poderiam trocar de lugar entre paginas e um deles
            // sumiria da leitura do analista sem nunca ter sido visto.
            .ThenByDescending(a => a.CriadoEm)
            .ThenBy(a => a.Id)
            .Skip(paginacao.QuantidadeAPular)
            .Take(paginacao.Tamanho)
            .ToListAsync(cancellationToken);

        return new Pagina<Alerta>(itens, paginacao, total);
    }

    /// <summary>
    /// Rastreado de proposito: o alerta devolvido aqui vai ser levado para um
    /// caso, o que muda o estado dele.
    /// </summary>
    public Task<Alerta?> BuscarPorIdAsync(Guid alertaId, CancellationToken cancellationToken) =>
        _contexto.Alertas.FirstOrDefaultAsync(a => a.Id == alertaId, cancellationToken);

    public async Task<IReadOnlyList<Alerta>> BuscarPorIdsAsync(
        IReadOnlyCollection<Guid> alertasIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alertasIds);

        if (alertasIds.Count == 0)
        {
            return [];
        }

        // Sem `AsNoTracking`: estes alertas vao mudar de estado ao entrar no
        // caso. E o filtro global cuida do tenant — um identificador de outra
        // organizacao simplesmente nao volta, e quem chamou trata a falta.
        return await _contexto.Alertas
            .Where(a => alertasIds.Contains(a.Id))
            .OrderByDescending(a => a.Score)
            .ToListAsync(cancellationToken);
    }

    public void Adicionar(Alerta alerta) => _contexto.Alertas.Add(alerta);
}
