using System.Security.Claims;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Infrastructure.Identidade;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CentralAntifraude.Api.Identidade;

/// <summary>
/// Le a identidade da requisicao atual a partir das claims do access token.
///
/// Este e o unico lugar do sistema que decide qual e o tenant de uma
/// requisicao. Nada aqui olha para corpo, query string ou cabecalho escolhido
/// pelo cliente — se olhasse, o isolamento entre organizacoes seria decidido
/// por quem chama a API (CLAUDE.md secao 9).
///
/// Claim ausente ou malformada resulta em anonimo, nunca em um tenant
/// arbitrario: <see cref="Guid.Empty"/> nao casa com nenhuma linha, entao o
/// filtro do EF Core devolve vazio em vez de tudo.
/// </summary>
public sealed class ContextoDoUsuarioAtual : IContextoDoUsuarioAtual
{
    private readonly IHttpContextAccessor _acessor;

    public ContextoDoUsuarioAtual(IHttpContextAccessor acessor) => _acessor = acessor;

    private ClaimsPrincipal? Principal => _acessor.HttpContext?.User;

    public bool EstaAutenticado =>
        Principal?.Identity?.IsAuthenticated == true &&
        OrganizacaoId != Guid.Empty &&
        UsuarioId != Guid.Empty &&
        Perfil is not null;

    public Guid OrganizacaoId => LerGuid(EmissorDeAccessToken.ClaimDeOrganizacao);

    public Guid UsuarioId => LerGuid(JwtRegisteredClaimNames.Sub);

    public PerfilDeUsuario? Perfil
    {
        get
        {
            var bruto = Principal?.FindFirstValue(EmissorDeAccessToken.ClaimDePerfil);

            // Enum.TryParse aceitaria "7" e devolveria um valor nao definido.
            // A checagem de IsDefined fecha essa porta: um perfil forjado no
            // token vira "sem perfil", nao um perfil desconhecido que passa
            // pelas politicas.
            return Enum.TryParse<PerfilDeUsuario>(bruto, ignoreCase: false, out var perfil)
                && Enum.IsDefined(perfil)
                    ? perfil
                    : null;
        }
    }

    private Guid LerGuid(string claim)
    {
        var bruto = Principal?.FindFirstValue(claim);

        return Guid.TryParse(bruto, out var valor) ? valor : Guid.Empty;
    }
}
