using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Primitivos;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.Infrastructure.Persistencia.Repositorios;

/// <summary>
/// Acesso a usuarios.
///
/// Todo <c>IgnoreQueryFilters</c> deste arquivo desliga apenas o filtro
/// nomeado de tenant, e cada um tem um comentario dizendo por que. Se um
/// dia aparecer aqui uma chamada sem justificativa, ela e o defeito.
/// </summary>
public sealed class RepositorioDeUsuarios : IRepositorioDeUsuarios
{
    private static readonly string[] FiltroDeTenant = [CentralAntifraudeDbContext.FiltroDeTenant];

    private readonly CentralAntifraudeDbContext _contexto;

    public RepositorioDeUsuarios(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    /// <summary>
    /// Login: descobrir a organizacao E o objetivo da consulta, entao ela nao
    /// pode ser filtrada pela organizacao. Este e o unico caminho do sistema
    /// que busca usuario por e-mail.
    /// </summary>
    public Task<Usuario?> BuscarPorEmailIgnorandoTenantAsync(
        Email email,
        CancellationToken cancellationToken) =>
        _contexto.Usuarios
            .IgnoreQueryFilters(FiltroDeTenant)
            .FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

    /// <summary>
    /// Renovacao de sessao: o refresh token chega antes de haver identidade
    /// na requisicao, entao ainda nao existe tenant para filtrar. O vinculo
    /// de seguranca aqui e o proprio token, que so o dono possui.
    /// </summary>
    public Task<Usuario?> BuscarPorIdIgnorandoTenantAsync(
        Guid usuarioId,
        CancellationToken cancellationToken) =>
        _contexto.Usuarios
            .IgnoreQueryFilters(FiltroDeTenant)
            .FirstOrDefaultAsync(u => u.Id == usuarioId, cancellationToken);

    /// <summary>
    /// Consulta normal, com o filtro de tenant ativo. Um identificador de
    /// outro tenant simplesmente nao retorna linha — e o chamador transforma
    /// isso em 404.
    /// </summary>
    public Task<Usuario?> BuscarPorIdAsync(Guid usuarioId, CancellationToken cancellationToken) =>
        _contexto.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId, cancellationToken);

    public async Task<Pagina<Usuario>> ListarAsync(
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paginacao);
        ArgumentNullException.ThrowIfNull(ordenacao);

        var consulta = _contexto.Usuarios.AsNoTracking();

        var total = await consulta.LongCountAsync(cancellationToken);

        // O switch traduz o nome canonico ja validado pela lista de
        // permitidos para uma expressao tipada. Nenhum texto do cliente
        // alcanca a montagem da consulta.
        var ascendente = ordenacao.Direcao == DirecaoDeOrdenacao.Ascendente;

        IOrderedQueryable<Usuario> ordenada = ordenacao.Campo switch
        {
            "email" => ascendente
                ? consulta.OrderBy(u => u.Email)
                : consulta.OrderByDescending(u => u.Email),
            "perfil" => ascendente
                ? consulta.OrderBy(u => u.Perfil)
                : consulta.OrderByDescending(u => u.Perfil),
            "criadoEm" => ascendente
                ? consulta.OrderBy(u => u.CriadoEm)
                : consulta.OrderByDescending(u => u.CriadoEm),
            _ => ascendente
                ? consulta.OrderBy(u => u.NomeCompleto)
                : consulta.OrderByDescending(u => u.NomeCompleto),
        };

        var itens = await ordenada
            // Desempate estavel: sem ele, duas paginas podem repetir ou pular
            // um registro quando ha nomes iguais.
            .ThenBy(u => u.Id)
            .Skip(paginacao.QuantidadeAPular)
            .Take(paginacao.Tamanho)
            .ToListAsync(cancellationToken);

        return new Pagina<Usuario>(itens, paginacao, total);
    }

    /// <summary>
    /// A restricao unica de e-mail no banco e global; checar so dentro da
    /// organizacao daria uma mensagem enganosa seguida de violacao de
    /// constraint na hora de gravar.
    /// </summary>
    public Task<bool> ExisteEmailIgnorandoTenantAsync(Email email, CancellationToken cancellationToken) =>
        _contexto.Usuarios
            .IgnoreQueryFilters(FiltroDeTenant)
            .AnyAsync(u => u.Email == email, cancellationToken);

    public void Adicionar(Usuario usuario) => _contexto.Usuarios.Add(usuario);
}

public sealed class RepositorioDeOrganizacoes : IRepositorioDeOrganizacoes
{
    private readonly CentralAntifraudeDbContext _contexto;

    public RepositorioDeOrganizacoes(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    // Organizacao nao tem filtro de tenant: ela e o tenant. O identificador
    // aqui sempre vem de uma identidade ja autenticada.
    public Task<Organizacao?> BuscarPorIdAsync(Guid organizacaoId, CancellationToken cancellationToken) =>
        _contexto.Organizacoes.FirstOrDefaultAsync(o => o.Id == organizacaoId, cancellationToken);

    public void Adicionar(Organizacao organizacao) => _contexto.Organizacoes.Add(organizacao);
}

/// <summary>
/// Acesso a refresh tokens.
///
/// Todas as consultas ignoram o filtro de tenant, e a razao e a mesma em
/// todas: o token e apresentado antes de existir identidade na requisicao —
/// o tenant e resultado da operacao, nao entrada dela. A prova de posse e o
/// proprio token, que so o dono tem.
/// </summary>
public sealed class RepositorioDeRefreshTokens : IRepositorioDeRefreshTokens
{
    private static readonly string[] FiltroDeTenant = [CentralAntifraudeDbContext.FiltroDeTenant];

    private readonly CentralAntifraudeDbContext _contexto;

    public RepositorioDeRefreshTokens(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    public Task<RefreshToken?> BuscarPorHashAsync(string hashDoToken, CancellationToken cancellationToken) =>
        _contexto.RefreshTokens
            .IgnoreQueryFilters(FiltroDeTenant)
            .FirstOrDefaultAsync(t => t.HashDoToken == hashDoToken, cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> ListarAtivosDaFamiliaAsync(
        Guid familiaId,
        CancellationToken cancellationToken) =>
        await _contexto.RefreshTokens
            .IgnoreQueryFilters(FiltroDeTenant)
            .Where(t => t.FamiliaId == familiaId && t.RevogadoEm == null)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> ListarAtivosDoUsuarioAsync(
        Guid usuarioId,
        CancellationToken cancellationToken) =>
        await _contexto.RefreshTokens
            .IgnoreQueryFilters(FiltroDeTenant)
            .Where(t => t.UsuarioId == usuarioId && t.RevogadoEm == null)
            .ToListAsync(cancellationToken);

    public void Adicionar(RefreshToken token) => _contexto.RefreshTokens.Add(token);
}

/// <summary>
/// Gravacao na trilha de auditoria. Somente insercao, por construcao: nao ha
/// metodo que atualize nem que remova.
/// </summary>
public sealed class RegistradorDeAuditoria : Application.Auditoria.IRegistradorDeAuditoria
{
    private readonly CentralAntifraudeDbContext _contexto;

    public RegistradorDeAuditoria(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    public Task RegistrarAsync(RegistroDeAuditoria registro, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registro);

        _contexto.RegistrosDeAuditoria.Add(registro);

        return Task.CompletedTask;
    }
}

public sealed class UnidadeDeTrabalho : IUnidadeDeTrabalho
{
    private readonly CentralAntifraudeDbContext _contexto;

    public UnidadeDeTrabalho(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    public Task<int> SalvarAsync(CancellationToken cancellationToken) =>
        _contexto.SaveChangesAsync(cancellationToken);
}
