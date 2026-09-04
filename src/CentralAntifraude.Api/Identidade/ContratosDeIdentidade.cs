using CentralAntifraude.Application.Comum;
using CentralAntifraude.Domain.Identidade;

namespace CentralAntifraude.Api.Identidade;

// ---------------------------------------------------------------------------
// DTOs de entrada.
//
// Explicitos e separados das entidades (CLAUDE.md secao 53). Com
// JsonUnmappedMemberHandling.Disallow ligado no Program.cs, um payload que
// traga "organizacaoId", "ativo" ou "id" e RECUSADO por conter campo
// desconhecido — o mass assignment nao e ignorado em silencio, ele vira 400.
// ---------------------------------------------------------------------------

public sealed record RequisicaoDeLogin(string? Email, string? Senha);

public sealed record RequisicaoDeCriacaoDeUsuario(
    string? Email,
    string? NomeCompleto,
    string? Senha,
    PerfilDeUsuario Perfil);

public sealed record RequisicaoDeAlteracaoDePerfil(PerfilDeUsuario Perfil);

public sealed record RequisicaoDeAlteracaoDeNome(string? NomeCompleto);

public sealed record RequisicaoDeAtivacao(bool Ativo);

public sealed record RequisicaoDeDefinicaoDeSenha(string? Senha);

// ---------------------------------------------------------------------------
// DTOs de saida.
// ---------------------------------------------------------------------------

/// <summary>
/// Resposta de login e de renovacao.
///
/// O refresh token NAO aparece aqui: ele sai apenas no cookie HttpOnly, fora
/// do alcance do JavaScript. Se viesse no corpo, o cookie perderia o sentido.
/// </summary>
public sealed record RespostaDeSessao(
    string AccessToken,
    DateTimeOffset ExpiraEm,
    UsuarioAutenticado Usuario);

/// <summary>Dados da propria sessao, usados pelo shell do frontend.</summary>
public sealed record UsuarioAutenticado(
    Guid Id,
    string Email,
    string NomeCompleto,
    PerfilDeUsuario Perfil,
    Guid OrganizacaoId)
{
    public static UsuarioAutenticado De(Usuario usuario)
    {
        ArgumentNullException.ThrowIfNull(usuario);

        return new UsuarioAutenticado(
            usuario.Id,
            usuario.Email.Valor,
            usuario.NomeCompleto,
            usuario.Perfil,
            usuario.OrganizacaoId);
    }
}

/// <summary>
/// Usuario na listagem administrativa.
/// Sem hash de senha, sem qualquer material de credencial.
/// </summary>
public sealed record UsuarioResumido(
    Guid Id,
    string Email,
    string NomeCompleto,
    PerfilDeUsuario Perfil,
    bool Ativo,
    DateTimeOffset CriadoEm)
{
    public static UsuarioResumido De(Usuario usuario)
    {
        ArgumentNullException.ThrowIfNull(usuario);

        return new UsuarioResumido(
            usuario.Id,
            usuario.Email.Valor,
            usuario.NomeCompleto,
            usuario.Perfil,
            usuario.Ativo,
            usuario.CriadoEm);
    }
}

/// <summary>Envelope de resposta paginada.</summary>
public sealed record RespostaPaginada<T>(
    IReadOnlyList<T> Itens,
    int Pagina,
    int Tamanho,
    long Total,
    int TotalDePaginas);

public static class RespostaPaginada
{
    public static RespostaPaginada<TDestino> De<TOrigem, TDestino>(
        Pagina<TOrigem> pagina,
        Func<TOrigem, TDestino> converter)
    {
        ArgumentNullException.ThrowIfNull(pagina);
        ArgumentNullException.ThrowIfNull(converter);

        return new RespostaPaginada<TDestino>(
            pagina.Itens.Select(converter).ToList(),
            pagina.PaginaAtual,
            pagina.TamanhoDaPagina,
            pagina.TotalDeItens,
            pagina.TotalDePaginas);
    }
}
