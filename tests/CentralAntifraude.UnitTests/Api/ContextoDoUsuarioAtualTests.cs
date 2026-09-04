using System.Security.Claims;
using CentralAntifraude.Api.Identidade;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Infrastructure.Identidade;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CentralAntifraude.UnitTests.Api;

/// <summary>
/// Este e o unico ponto do sistema que decide qual e o tenant de uma
/// requisicao. Um erro aqui nao aparece como falha: aparece como um cliente
/// enxergando dados de outro.
/// </summary>
public sealed class ContextoDoUsuarioAtualTests
{
    private static readonly Guid OrganizacaoValida = Guid.Parse("01a0699c-676d-78ca-a854-bfc4a78c1fa6");
    private static readonly Guid UsuarioValido = Guid.Parse("01a0699c-68c5-7e77-b49d-62a43bee10a8");

    [Fact]
    public void Le_organizacao_usuario_e_perfil_das_claims()
    {
        var contexto = Montar(
            (JwtRegisteredClaimNames.Sub, UsuarioValido.ToString()),
            (EmissorDeAccessToken.ClaimDeOrganizacao, OrganizacaoValida.ToString()),
            (EmissorDeAccessToken.ClaimDePerfil, nameof(PerfilDeUsuario.SupervisorDeFraude)));

        Assert.True(contexto.EstaAutenticado);
        Assert.Equal(OrganizacaoValida, contexto.OrganizacaoId);
        Assert.Equal(UsuarioValido, contexto.UsuarioId);
        Assert.Equal(PerfilDeUsuario.SupervisorDeFraude, contexto.Perfil);
    }

    [Fact]
    public void Sem_autenticacao_o_tenant_e_vazio()
    {
        // Guid.Empty nao casa com nenhuma linha, entao o filtro do EF devolve
        // NADA. Se fosse nulo tratado como "sem filtro", devolveria TUDO.
        var contexto = new ContextoDoUsuarioAtual(AcessorCom(new DefaultHttpContext()));

        Assert.False(contexto.EstaAutenticado);
        Assert.Equal(Guid.Empty, contexto.OrganizacaoId);
        Assert.Null(contexto.Perfil);
    }

    [Fact]
    public void Sem_HttpContext_o_tenant_e_vazio()
    {
        var contexto = new ContextoDoUsuarioAtual(new HttpContextAccessor());

        Assert.False(contexto.EstaAutenticado);
        Assert.Equal(Guid.Empty, contexto.OrganizacaoId);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("99")]
    [InlineData("Superusuario")]
    [InlineData("administrador")]
    [InlineData("")]
    [InlineData("Administrador, Auditor")]
    public void Perfil_forjado_no_token_nao_vira_perfil_valido(string perfilBruto)
    {
        // Enum.TryParse sozinho aceitaria "7" e devolveria (PerfilDeUsuario)7,
        // um valor que nao existe mas passaria por qualquer comparacao
        // descuidada. A checagem de IsDefined fecha essa porta.
        // "administrador" em minusculas tambem e recusado: a comparacao e
        // sensivel a caixa, igual ao que o emissor grava.
        var contexto = Montar(
            (JwtRegisteredClaimNames.Sub, UsuarioValido.ToString()),
            (EmissorDeAccessToken.ClaimDeOrganizacao, OrganizacaoValida.ToString()),
            (EmissorDeAccessToken.ClaimDePerfil, perfilBruto));

        Assert.Null(contexto.Perfil);
        Assert.False(contexto.EstaAutenticado);
    }

    [Theory]
    [InlineData("nao-e-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("' OR 1=1 --")]
    public void Organizacao_malformada_na_claim_vira_tenant_vazio(string organizacaoBruta)
    {
        var contexto = Montar(
            (JwtRegisteredClaimNames.Sub, UsuarioValido.ToString()),
            (EmissorDeAccessToken.ClaimDeOrganizacao, organizacaoBruta),
            (EmissorDeAccessToken.ClaimDePerfil, nameof(PerfilDeUsuario.Administrador)));

        Assert.Equal(Guid.Empty, contexto.OrganizacaoId);
        Assert.False(contexto.EstaAutenticado);
    }

    [Fact]
    public void Token_sem_claim_de_organizacao_nao_autentica()
    {
        var contexto = Montar(
            (JwtRegisteredClaimNames.Sub, UsuarioValido.ToString()),
            (EmissorDeAccessToken.ClaimDePerfil, nameof(PerfilDeUsuario.Administrador)));

        Assert.False(contexto.EstaAutenticado);
        Assert.Equal(Guid.Empty, contexto.OrganizacaoId);
    }

    private static ContextoDoUsuarioAtual Montar(params (string Tipo, string Valor)[] claims)
    {
        var identidade = new ClaimsIdentity(
            claims.Select(c => new Claim(c.Tipo, c.Valor)),
            authenticationType: "Teste");

        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identidade) };

        return new ContextoDoUsuarioAtual(AcessorCom(httpContext));
    }

    private static IHttpContextAccessor AcessorCom(HttpContext contexto) =>
        new HttpContextAccessor { HttpContext = contexto };
}
