using System.Security.Claims;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Domain.Identidade;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CentralAntifraude.Infrastructure.Identidade;

/// <summary>
/// Emite o access token JWT da sessao humana.
///
/// O token carrega o minimo necessario para autorizar uma requisicao sem ir
/// ao banco: quem e, de qual organizacao e com qual perfil. Nada alem disso —
/// e-mail, nome e qualquer outro dado pessoal ficam fora, porque um JWT
/// trafega em toda requisicao, aparece em log de proxy e e apenas assinado,
/// nunca cifrado. Quem tem o token le o conteudo.
/// </summary>
public sealed class EmissorDeAccessToken : IEmissorDeAccessToken
{
    /// <summary>Claim com o tenant. Le-se do token, jamais do payload.</summary>
    public const string ClaimDeOrganizacao = "org";

    /// <summary>Claim com o perfil RBAC.</summary>
    public const string ClaimDePerfil = "perfil";

    private readonly OpcoesDeAutenticacao _opcoes;
    private readonly SigningCredentials _credenciais;
    private readonly JsonWebTokenHandler _manipulador = new();

    public EmissorDeAccessToken(OpcoesDeAutenticacao opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        _opcoes = opcoes;
        _credenciais = new SigningCredentials(
            new SymmetricSecurityKey(opcoes.LerChave()),
            SecurityAlgorithms.HmacSha256);
    }

    public AccessTokenEmitido Emitir(Usuario usuario, DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(usuario);

        var expiraEm = agora + _opcoes.ValidadeDoAccessToken;

        var descritor = new SecurityTokenDescriptor
        {
            Issuer = _opcoes.Emissor,
            Audience = _opcoes.Audiencia,
            IssuedAt = agora.UtcDateTime,
            NotBefore = agora.UtcDateTime,
            Expires = expiraEm.UtcDateTime,
            SigningCredentials = _credenciais,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtRegisteredClaimNames.Sub] = usuario.Id.ToString(),
                // jti unico permite rastrear um token especifico no log sem
                // registrar o token inteiro.
                [JwtRegisteredClaimNames.Jti] = Domain.Primitivos.Identificador.Novo().ToString(),
                [ClaimDeOrganizacao] = usuario.OrganizacaoId.ToString(),
                [ClaimDePerfil] = usuario.Perfil.ToString(),
            },
        };

        return new AccessTokenEmitido(_manipulador.CreateToken(descritor), expiraEm);
    }

    /// <summary>
    /// Parametros de validacao usados pela API.
    ///
    /// Ficam ao lado da emissao de proposito: emissor e validador precisam
    /// concordar, e mante-los em arquivos diferentes e como ter duas copias
    /// da mesma chave — uma delas vai ficar desatualizada.
    /// </summary>
    public static TokenValidationParameters MontarParametrosDeValidacao(OpcoesDeAutenticacao opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = opcoes.Emissor,

            ValidateAudience = true,
            ValidAudience = opcoes.Audiencia,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(opcoes.LerChave()),

            // Sem isto, um token com "alg": "none" ou assinado com outro
            // algoritmo poderia ser aceito - a familia classica de ataques
            // contra JWT.
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],

            ValidateLifetime = true,

            // O padrao da biblioteca e 5 minutos de tolerancia, o que estende
            // na pratica um access token de 15 para 20 minutos. Emissor e
            // validador aqui sao o mesmo processo: nao ha relogios distintos
            // para conciliar.
            ClockSkew = TimeSpan.Zero,

            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = ClaimTypes.Role,
        };
    }
}
