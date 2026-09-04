using CentralAntifraude.Application.Comum;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Application.Identidade;

/// <summary>
/// Acesso a usuarios.
///
/// Nao e um repositorio generico: cada metodo existe para um caso de uso
/// concreto, e os dois que atravessam o isolamento de tenant estao marcados
/// no nome. Isso e deliberado — quem le uma chamada a
/// <see cref="BuscarPorEmailIgnorandoTenantAsync"/> ve na hora que ali o
/// filtro global nao vale, e pode perguntar por que.
/// </summary>
public interface IRepositorioDeUsuarios
{
    /// <summary>
    /// Busca por e-mail sem filtro de tenant.
    ///
    /// E a unica forma possivel de fazer login: antes de autenticar, nao se
    /// sabe a que organizacao a pessoa pertence — descobrir isso e justamente
    /// o resultado da autenticacao. Por isso o e-mail e unico globalmente
    /// (docs/adr/0005).
    ///
    /// Nenhum outro fluxo pode usar este metodo.
    /// </summary>
    Task<Usuario?> BuscarPorEmailIgnorandoTenantAsync(Email email, CancellationToken cancellationToken);

    /// <summary>
    /// Busca por id sem filtro de tenant, usada no refresh, quando ainda nao
    /// ha contexto de usuario estabelecido na requisicao.
    /// </summary>
    Task<Usuario?> BuscarPorIdIgnorandoTenantAsync(Guid usuarioId, CancellationToken cancellationToken);

    /// <summary>Busca dentro do tenant atual. Devolve nulo para id de outro tenant.</summary>
    Task<Usuario?> BuscarPorIdAsync(Guid usuarioId, CancellationToken cancellationToken);

    /// <summary>Lista os usuarios do tenant atual.</summary>
    Task<Pagina<Usuario>> ListarAsync(
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken);

    /// <summary>
    /// Verifica a existencia do e-mail globalmente.
    ///
    /// Precisa ignorar o tenant: a restricao unica do banco tambem e global,
    /// e checar so dentro da organizacao daria uma mensagem de erro enganosa
    /// seguida de uma violacao de constraint.
    /// </summary>
    Task<bool> ExisteEmailIgnorandoTenantAsync(Email email, CancellationToken cancellationToken);

    void Adicionar(Usuario usuario);
}

/// <summary>Acesso a organizacoes.</summary>
public interface IRepositorioDeOrganizacoes
{
    Task<Organizacao?> BuscarPorIdAsync(Guid organizacaoId, CancellationToken cancellationToken);

    void Adicionar(Organizacao organizacao);
}

/// <summary>
/// Acesso a refresh tokens.
///
/// Todas as buscas ignoram o filtro de tenant porque a apresentacao de um
/// refresh token acontece ANTES de existir identidade na requisicao — o
/// tenant e um resultado da operacao, nao uma entrada dela.
/// </summary>
public interface IRepositorioDeRefreshTokens
{
    Task<RefreshToken?> BuscarPorHashAsync(string hashDoToken, CancellationToken cancellationToken);

    /// <summary>Tokens vivos de uma familia, para revogacao em bloco.</summary>
    Task<IReadOnlyList<RefreshToken>> ListarAtivosDaFamiliaAsync(
        Guid familiaId,
        CancellationToken cancellationToken);

    /// <summary>Tokens vivos de um usuario, para revogar acesso por completo.</summary>
    Task<IReadOnlyList<RefreshToken>> ListarAtivosDoUsuarioAsync(
        Guid usuarioId,
        CancellationToken cancellationToken);

    void Adicionar(RefreshToken token);
}

/// <summary>
/// Fecha a transacao implicita do DbContext.
///
/// Existe para que os servicos de aplicacao possam agrupar varias alteracoes
/// em uma unica gravacao — o caso concreto e o refresh, em que revogar o
/// token antigo, criar o novo e gravar a auditoria precisam acontecer juntos
/// ou nao acontecer.
/// </summary>
public interface IUnidadeDeTrabalho
{
    Task<int> SalvarAsync(CancellationToken cancellationToken);
}
