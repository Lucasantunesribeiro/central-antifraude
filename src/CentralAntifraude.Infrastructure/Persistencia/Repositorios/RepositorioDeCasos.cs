using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Investigacao;
using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Investigacao;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.Infrastructure.Persistencia.Repositorios;

/// <summary>
/// Acesso aos casos.
///
/// **A timeline e as notas nao sao navegacao do caso.** Elas sao consultadas
/// por conta propria na leitura e gravadas explicitamente na escrita. Alem de
/// evitar carregar a historia inteira para mudar um responsavel, isso fecha um
/// defeito silencioso do EF: uma entidade nova com chave ja preenchida dentro
/// de uma colecao de navegacao e tratada como linha existente, e o
/// <c>UPDATE</c> de zero linhas vira um falso conflito de concorrencia.
/// </summary>
public sealed class RepositorioDeCasos : IRepositorioDeCasos
{
    private readonly CentralAntifraudeDbContext _contexto;

    public RepositorioDeCasos(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    public async Task<Pagina<Caso>> ListarAsync(
        FiltroDeCasos filtro,
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        ArgumentNullException.ThrowIfNull(paginacao);
        ArgumentNullException.ThrowIfNull(ordenacao);

        // O tenant nao aparece em nenhum `Where` daqui: quem o aplica e o
        // filtro global do DbContext.
        var consulta = _contexto.Casos.AsNoTracking();

        if (filtro.Status is { } status)
        {
            consulta = consulta.Where(c => c.Status == status);
        }

        if (filtro.Resultado is { } resultado)
        {
            consulta = consulta.Where(c => c.Resultado == resultado);
        }

        if (filtro.ResponsavelId is { } responsavel)
        {
            consulta = consulta.Where(c => c.ResponsavelId == responsavel);
        }

        if (filtro.SemResponsavel)
        {
            consulta = consulta.Where(c => c.ResponsavelId == null);
        }

        var total = await consulta.LongCountAsync(cancellationToken);

        var ascendente = ordenacao.Direcao == DirecaoDeOrdenacao.Ascendente;

        IOrderedQueryable<Caso> ordenada = ordenacao.Campo switch
        {
            "abertoEm" => ascendente
                ? consulta.OrderBy(c => c.AbertoEm)
                : consulta.OrderByDescending(c => c.AbertoEm),
            _ => ascendente
                ? consulta.OrderBy(c => c.AtualizadoEm)
                : consulta.OrderByDescending(c => c.AtualizadoEm),
        };

        var itens = await ordenada
            // Desempate estavel: sem ele, dois casos tocados no mesmo instante
            // poderiam trocar de lugar entre paginas e um deles sumiria da
            // leitura sem nunca ter sido visto.
            .ThenBy(c => c.Id)
            .Skip(paginacao.QuantidadeAPular)
            .Take(paginacao.Tamanho)
            .ToListAsync(cancellationToken);

        return new Pagina<Caso>(itens, paginacao, total);
    }

    /// <summary>
    /// Rastreado de proposito: o retorno vai ser alterado e gravado, e o token
    /// de concorrencia precisa do valor original para montar o
    /// <c>WHERE versao = @lida</c> do <c>UPDATE</c>.
    /// </summary>
    public Task<Caso?> BuscarPorIdAsync(Guid casoId, CancellationToken cancellationToken) =>
        _contexto.Casos.FirstOrDefaultAsync(c => c.Id == casoId, cancellationToken);

    public async Task<IReadOnlyList<EventoDoCaso>> ListarTimelineAsync(
        Guid casoId,
        CancellationToken cancellationToken) =>
        await _contexto.EventosDoCaso
            .AsNoTracking()
            .Where(e => e.CasoId == casoId)
            // Pela SEQUENCIA, e nao pelo horario: abrir um caso grava dois
            // eventos no mesmo instante, e o UUIDv7 nao desempata porque
            // sorteia os bits finais.
            .OrderBy(e => e.Sequencia)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<NotaDoCaso>> ListarNotasAsync(
        Guid casoId,
        CancellationToken cancellationToken) =>
        await _contexto.NotasDoCaso
            .AsNoTracking()
            .Where(n => n.CasoId == casoId)
            .OrderBy(n => n.CriadaEm)
            .ThenBy(n => n.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Alerta>> ListarAlertasDoCasoAsync(
        Guid casoId,
        CancellationToken cancellationToken) =>
        await _contexto.Alertas
            .Where(a => a.CasoId == casoId)
            .OrderByDescending(a => a.Score)
            .ThenBy(a => a.CriadoEm)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, ResumoDosAlertas>> ResumirAlertasAsync(
        IReadOnlyCollection<Guid> casosIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(casosIds);

        if (casosIds.Count == 0)
        {
            return new Dictionary<Guid, ResumoDosAlertas>();
        }

        // Uma agregacao no banco para a pagina inteira. Trazer os alertas e
        // contar em memoria funcionaria com dez casos e cairia com mil.
        var linhas = await _contexto.Alertas
            .AsNoTracking()
            .Where(a => a.CasoId != null && casosIds.Contains(a.CasoId!.Value))
            .GroupBy(a => a.CasoId!.Value)
            .Select(grupo => new
            {
                CasoId = grupo.Key,
                Quantidade = grupo.Count(),
                MaiorScore = grupo.Max(a => a.Score),

                // **Nao `Max(a => a.Prioridade)`.** A prioridade e gravada como
                // texto, e o maximo de texto e alfabetico: "Media" venceria
                // "Alta", que e o oposto da gravidade. E a mesma armadilha que
                // a Fase 6 encontrou na ordenacao da fila (ADR 0011, decisao 8),
                // e aqui ela seria ainda mais silenciosa — a lista mostraria
                // "Media" num caso que contem um bloqueio.
                TemAlta = grupo.Max(a => a.Prioridade == PrioridadeDeAlerta.Alta ? 1 : 0),
            })
            .ToListAsync(cancellationToken);

        return linhas.ToDictionary(
            linha => linha.CasoId,
            linha => new ResumoDosAlertas(
                linha.Quantidade,
                linha.MaiorScore,
                linha.TemAlta == 1 ? PrioridadeDeAlerta.Alta : PrioridadeDeAlerta.Media));
    }

    public void Adicionar(Caso caso)
    {
        _contexto.Casos.Add(caso);

        RegistrarNovidades(caso);
    }

    public void RegistrarNovidades(Caso caso)
    {
        ArgumentNullException.ThrowIfNull(caso);

        _contexto.EventosDoCaso.AddRange(caso.NovosEventos);
        _contexto.NotasDoCaso.AddRange(caso.NovasNotas);
    }

    public void AdicionarVeredictos(
        IReadOnlyCollection<ResultadoDeInvestigacaoDaTransacao> veredictos)
    {
        ArgumentNullException.ThrowIfNull(veredictos);

        _contexto.ResultadosDeInvestigacao.AddRange(veredictos);
    }
}
