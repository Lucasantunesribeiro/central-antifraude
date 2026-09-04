using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Integracoes;
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

    public async Task<Pagina<Transacao>> ListarAsync(
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paginacao);
        ArgumentNullException.ThrowIfNull(ordenacao);

        var consulta = _contexto.Transacoes.AsNoTracking();
        var total = await consulta.LongCountAsync(cancellationToken);

        var ascendente = ordenacao.Direcao == DirecaoDeOrdenacao.Ascendente;

        IOrderedQueryable<Transacao> ordenada = ordenacao.Campo switch
        {
            "ocorridaEm" => ascendente
                ? consulta.OrderBy(t => t.OcorridaEm)
                : consulta.OrderByDescending(t => t.OcorridaEm),
            "valor" => ascendente
                ? consulta.OrderBy(t => t.Valor.Valor)
                : consulta.OrderByDescending(t => t.Valor.Valor),
            _ => ascendente
                ? consulta.OrderBy(t => t.RecebidaEm)
                : consulta.OrderByDescending(t => t.RecebidaEm),
        };

        var itens = await ordenada
            .ThenBy(t => t.Id)
            .Skip(paginacao.QuantidadeAPular)
            .Take(paginacao.Tamanho)
            .ToListAsync(cancellationToken);

        return new Pagina<Transacao>(itens, paginacao, total);
    }

    public void Adicionar(Transacao transacao) => _contexto.Transacoes.Add(transacao);
}
