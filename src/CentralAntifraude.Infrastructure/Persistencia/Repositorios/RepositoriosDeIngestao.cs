using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Integracoes;
using CentralAntifraude.Application.Risco;
using CentralAntifraude.Application.Transacoes;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Integracoes;
using CentralAntifraude.Domain.Transacoes;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.Infrastructure.Persistencia.Repositorios;

/// <summary>
/// Acesso a integracoes e credenciais.
///
/// Como no repositorio de usuarios, os metodos que atravessam o isolamento de
/// tenant dizem isso no nome, e cada um tem um comentario com a razao.
/// </summary>
public sealed class RepositorioDeIntegracoes : IRepositorioDeIntegracoes
{
    private static readonly string[] FiltroDeTenant = [CentralAntifraudeDbContext.FiltroDeTenant];

    private readonly CentralAntifraudeDbContext _contexto;

    public RepositorioDeIntegracoes(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    public Task<Integracao?> BuscarPorIdAsync(Guid integracaoId, CancellationToken cancellationToken) =>
        _contexto.Integracoes.FirstOrDefaultAsync(i => i.Id == integracaoId, cancellationToken);

    public async Task<Pagina<Integracao>> ListarAsync(
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paginacao);
        ArgumentNullException.ThrowIfNull(ordenacao);

        var consulta = _contexto.Integracoes.AsNoTracking();
        var total = await consulta.LongCountAsync(cancellationToken);

        var ascendente = ordenacao.Direcao == DirecaoDeOrdenacao.Ascendente;

        IOrderedQueryable<Integracao> ordenada = ordenacao.Campo switch
        {
            "criadaEm" => ascendente
                ? consulta.OrderBy(i => i.CriadaEm)
                : consulta.OrderByDescending(i => i.CriadaEm),
            _ => ascendente
                ? consulta.OrderBy(i => i.Nome)
                : consulta.OrderByDescending(i => i.Nome),
        };

        var itens = await ordenada
            .ThenBy(i => i.Id)
            .Skip(paginacao.QuantidadeAPular)
            .Take(paginacao.Tamanho)
            .ToListAsync(cancellationToken);

        return new Pagina<Integracao>(itens, paginacao, total);
    }

    public async Task<IReadOnlyList<CredencialDeIntegracao>> ListarCredenciaisAsync(
        Guid integracaoId,
        CancellationToken cancellationToken) =>
        await _contexto.CredenciaisDeIntegracao
            .Where(c => c.IntegracaoId == integracaoId)
            .OrderByDescending(c => c.CriadaEm)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Autenticacao da integracao: descobrir a organizacao E o objetivo, entao
    /// nao ha tenant pelo qual filtrar. A prova de posse e o segredo, conferido
    /// pelo chamador contra o hash desta linha.
    /// </summary>
    public Task<CredencialDeIntegracao?> BuscarCredencialPorIdentificadorPublicoIgnorandoTenantAsync(
        string identificadorPublico,
        CancellationToken cancellationToken) =>
        _contexto.CredenciaisDeIntegracao
            .IgnoreQueryFilters(FiltroDeTenant)
            .FirstOrDefaultAsync(c => c.IdentificadorPublico == identificadorPublico, cancellationToken);

    /// <summary>
    /// Estado da integracao e da organizacao dona, ainda durante a
    /// autenticacao — antes de existir contexto de tenant.
    ///
    /// Uma credencial valida nao basta: se a integracao foi desativada ou a
    /// organizacao inteira foi suspensa, a chave nao vale mais.
    /// </summary>
    public async Task<(Integracao Integracao, bool OrganizacaoAtiva)?>
        BuscarContextoDaCredencialIgnorandoTenantAsync(
            Guid integracaoId,
            CancellationToken cancellationToken)
    {
        var resultado = await _contexto.Integracoes
            .IgnoreQueryFilters(FiltroDeTenant)
            .Where(i => i.Id == integracaoId)
            .Join(
                _contexto.Organizacoes,
                integracao => integracao.OrganizacaoId,
                organizacao => organizacao.Id,
                (integracao, organizacao) => new { integracao, organizacao.Ativa })
            .FirstOrDefaultAsync(cancellationToken);

        return resultado is null ? null : (resultado.integracao, resultado.Ativa);
    }

    public void Adicionar(Integracao integracao) => _contexto.Integracoes.Add(integracao);

    public void AdicionarCredencial(CredencialDeIntegracao credencial) =>
        _contexto.CredenciaisDeIntegracao.Add(credencial);
}

/// <summary>
/// Acesso a transacoes.
///
/// Todas as consultas respeitam o filtro de tenant. A ingestao ja roda com o
/// contexto da integracao autenticada estabelecido, entao aqui nao ha exececao
/// a fazer — ao contrario da autenticacao, que acontece antes.
/// </summary>
public sealed class RepositorioDeTransacoes : IRepositorioDeTransacoes
{
    private readonly CentralAntifraudeDbContext _contexto;

    public RepositorioDeTransacoes(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    public Task<Transacao?> BuscarPorChaveDeIdempotenciaAsync(
        Guid integracaoId,
        string chaveDeIdempotencia,
        CancellationToken cancellationToken) =>
        _contexto.Transacoes.FirstOrDefaultAsync(
            t => t.IntegracaoId == integracaoId && t.ChaveDeIdempotencia == chaveDeIdempotencia,
            cancellationToken);

    public Task<Transacao?> BuscarPorIdentificadorExternoAsync(
        Guid integracaoId,
        string identificadorExterno,
        CancellationToken cancellationToken) =>
        _contexto.Transacoes.FirstOrDefaultAsync(
            t => t.IntegracaoId == integracaoId && t.IdentificadorExterno == identificadorExterno,
            cancellationToken);

    public Task<Transacao?> BuscarPorIdAsync(Guid transacaoId, CancellationToken cancellationToken) =>
        _contexto.Transacoes.FirstOrDefaultAsync(t => t.Id == transacaoId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, Transacao>> BuscarPorIdsAsync(
        IReadOnlyCollection<Guid> transacoesIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transacoesIds);

        if (transacoesIds.Count == 0)
        {
            return new Dictionary<Guid, Transacao>();
        }

        var transacoes = await _contexto.Transacoes
            .AsNoTracking()
            .Where(t => transacoesIds.Contains(t.Id))
            .ToListAsync(cancellationToken);

        return transacoes.ToDictionary(t => t.Id);
    }

    /// <summary>
    /// O console de transacoes: filtro, ordenacao e paginacao em uma consulta.
    ///
    /// **A juncao decide o total.** Contar antes de filtrar devolveria um
    /// total maior do que a lista, e a paginacao mostraria paginas vazias no
    /// fim — o tipo de defeito que ninguem reporta e todo mundo desconfia.
    ///
    /// **Nada do que o cliente digitou vira SQL.** O campo de ordenacao vem do
    /// vocabulario fechado de <see cref="ParametrosDeOrdenacao"/>; os valores
    /// do filtro sao enums resolvidos ou numeros em faixa; e a busca livre vai
    /// como parametro de `ILIKE` com os curingas ja escapados
    /// (<see cref="FiltroDeTransacoes.PadraoDeBusca"/>).
    /// </summary>
    public async Task<Pagina<TransacaoAvaliada>> ListarComAvaliacaoAsync(
        FiltroDeTransacoes filtro,
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        ArgumentNullException.ThrowIfNull(paginacao);
        ArgumentNullException.ThrowIfNull(ordenacao);

        // **A juncao e a projecao ficam nesta funcao, e nao num ajudante.** O
        // par intermediario precisa ser um tipo ANONIMO: o EF Core enxerga
        // atraves dele para ordenar depois da juncao, mas nao enxerga atraves
        // de um record posicional — com `new Linha(a, b).Transacao.RecebidaEm`
        // no `ORDER BY`, a consulta inteira deixa de ser traduzivel.
        var consulta = from transacao in Filtrar(filtro)
                       join avaliacao in FiltrarAvaliacoes(filtro)
                           on transacao.Id equals avaliacao.TransacaoId into juncao
                       from avaliacao in juncao.DefaultIfEmpty()
                       select new { Transacao = transacao, Avaliacao = avaliacao };

        // A juncao e a esquerda por padrao — transacoes gravadas na Fase 2
        // existem sem avaliacao, e some-las da lista esconderia dado real.
        // Quando o filtro fala de score, decisao ou sinal, ela vira efetiva:
        // filtrar por score exclui, por definicao, o que nao tem score.
        if (filtro.ExigeAvaliacao)
        {
            consulta = consulta.Where(l => l.Avaliacao != null);
        }

        // Contar DEPOIS de filtrar: um total maior do que a lista faria a
        // paginacao mostrar paginas vazias no fim, e ninguem reporta isso —
        // so deixa de confiar na contagem.
        var total = await consulta.LongCountAsync(cancellationToken);

        var ascendente = ordenacao.Direcao == DirecaoDeOrdenacao.Ascendente;

        // Ordenar por score exige tratar a ausencia: uma transacao sem
        // avaliacao nao tem score, e deixar o banco decidir onde colocar nulos
        // faria a ordem mudar entre bancos. As sem avaliacao vao para o fim,
        // nas duas direcoes.
        var ordenada = ordenacao.Campo switch
        {
            "ocorridaEm" => ascendente
                ? consulta.OrderBy(l => l.Transacao.OcorridaEm)
                : consulta.OrderByDescending(l => l.Transacao.OcorridaEm),
            "valor" => ascendente
                ? consulta.OrderBy(l => l.Transacao.Valor.Valor)
                : consulta.OrderByDescending(l => l.Transacao.Valor.Valor),
            "score" => ascendente
                ? consulta.OrderBy(l => l.Avaliacao == null).ThenBy(l => l.Avaliacao!.Score)
                : consulta.OrderBy(l => l.Avaliacao == null).ThenByDescending(l => l.Avaliacao!.Score),
            _ => ascendente
                ? consulta.OrderBy(l => l.Transacao.RecebidaEm)
                : consulta.OrderByDescending(l => l.Transacao.RecebidaEm),
        };

        var linhas = await ordenada
            // Desempate estavel: duas transacoes com o mesmo horario nao podem
            // trocar de lugar entre duas leituras da mesma pagina.
            .ThenBy(l => l.Transacao.Id)
            .Skip(paginacao.QuantidadeAPular)
            .Take(paginacao.Tamanho)
            .ToListAsync(cancellationToken);

        return new Pagina<TransacaoAvaliada>(
            [.. linhas.Select(l => new TransacaoAvaliada(l.Transacao, l.Avaliacao))],
            paginacao,
            total);
    }

    /// <summary>
    /// Transacoes do tenant, com o que o filtro diz sobre elas.
    ///
    /// **Nada do que o cliente digitou vira SQL.** O periodo vem tipado e a
    /// busca livre vai como parametro de `ILIKE` com os curingas ja escapados
    /// (<see cref="FiltroDeTransacoes.PadraoDeBusca"/>).
    /// </summary>
    private IQueryable<Transacao> Filtrar(FiltroDeTransacoes filtro)
    {
        var transacoes = _contexto.Transacoes.AsNoTracking();

        if (filtro.PadraoDeBusca is { } padrao)
        {
            transacoes = transacoes.Where(t =>
                EF.Functions.ILike(t.IdentificadorExterno, padrao, "\\")
                || EF.Functions.ILike(t.ClienteExternoId, padrao, "\\"));
        }

        if (filtro.De is { } de)
        {
            transacoes = transacoes.Where(t => t.OcorridaEm >= de);
        }

        if (filtro.Ate is { } ate)
        {
            transacoes = transacoes.Where(t => t.OcorridaEm <= ate);
        }

        return transacoes;
    }

    /// <summary>Avaliacoes do tenant, com o que o filtro diz sobre elas.</summary>
    private IQueryable<AvaliacaoDeRisco> FiltrarAvaliacoes(FiltroDeTransacoes filtro)
    {
        // `IgnoreAutoIncludes` nao e otimizacao: a avaliacao traz os sinais por
        // `AutoInclude`, e uma colecao dentro de uma juncao a esquerda nao tem
        // traducao para SQL — o EF recusa a consulta inteira. A listagem
        // tambem nao mostra sinal nenhum; quem precisa deles e o detalhe, que
        // busca a avaliacao pelo caminho proprio.
        var avaliacoes = _contexto.AvaliacoesDeRisco.AsNoTracking().IgnoreAutoIncludes();

        if (filtro.Decisao is { } decisao)
        {
            avaliacoes = avaliacoes.Where(a => a.Decisao == decisao);
        }

        if (filtro.ScoreMinimo is { } minimo)
        {
            avaliacoes = avaliacoes.Where(a => a.Score >= minimo);
        }

        if (filtro.ScoreMaximo is { } maximo)
        {
            avaliacoes = avaliacoes.Where(a => a.Score <= maximo);
        }

        if (filtro.TipoDeRegra is { } tipo)
        {
            // Subconsulta em vez de juncao com os sinais: uma avaliacao com
            // tres sinais do mesmo tipo apareceria tres vezes numa juncao, e a
            // pagina traria a mesma transacao repetida.
            avaliacoes = avaliacoes.Where(a => _contexto.Set<SinalDeRisco>()
                .Any(s => s.AvaliacaoId == a.Id && s.Tipo == tipo));
        }

        return avaliacoes;
    }

    public void Adicionar(Transacao transacao) => _contexto.Transacoes.Add(transacao);
}
