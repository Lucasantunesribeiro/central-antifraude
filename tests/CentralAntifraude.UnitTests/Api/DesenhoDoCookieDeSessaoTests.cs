using CentralAntifraude.Api.Identidade;
using Microsoft.AspNetCore.Http;

namespace CentralAntifraude.UnitTests.Api;

/// <summary>
/// O `SameSite` do cookie de sessão, que quase custou a sessão inteira em
/// produção.
///
/// **O defeito.** O frontend (Vercel) e a API (Lambda Function URL) estão em
/// sites diferentes. Um cookie `SameSite=Lax` não é enviado pelo navegador numa
/// requisição `fetch` cross-site — então o `refresh` que o app dispara ao montar
/// não recebia o cookie, e todo F5 deslogava. O login funcionava na sessão
/// corrente, o que tornava o defeito invisível até alguém recarregar a página.
///
/// **Por que nenhum teste anterior pegou.** Os testes de integração rodam sobre
/// `WebApplicationFactory`, que serve HTTP — e em HTTP a escolha certa continua
/// sendo `Lax` (um cookie `None` sem `Secure` o navegador descarta). A decisão
/// só se separa em HTTPS, e nenhum teste exercitava esse ramo.
///
/// Estes testes exercitam os dois ramos diretamente, sem subir servidor.
/// </summary>
public sealed class DesenhoDoCookieDeSessaoTests
{
    private static readonly DateTimeOffset Expira =
        new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    private static HttpContext ContextoCom(bool https)
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.Scheme = https ? "https" : "http";
        contexto.Request.Host = new HostString(https ? "api.exemplo" : "localhost");
        return contexto;
    }

    /// <summary>
    /// **A asserção que o deploy real exigiu.** Em HTTPS, o cookie precisa de
    /// `None` para cruzar de `vercel.app` para `on.aws`; qualquer outro valor
    /// mata a persistência de sessão no navegador.
    /// </summary>
    [Fact]
    public void Em_HTTPS_o_cookie_e_SameSite_None_para_cruzar_sites()
    {
        var opcoes = SessaoHttp.MontarOpcoes(ContextoCom(https: true), Expira);

        Assert.Equal(SameSiteMode.None, opcoes.SameSite);

        // `None` sem `Secure` é descartado pelo navegador — os dois andam juntos.
        Assert.True(opcoes.Secure);
    }

    /// <summary>
    /// Em HTTP local, ao contrário, `None` seria descartado por falta de
    /// `Secure`. Ali `Lax` é o correto, e o cookie não precisa cruzar site
    /// nenhum porque o proxy do Vite compartilha a origem.
    /// </summary>
    [Fact]
    public void Em_HTTP_local_o_cookie_continua_Lax_e_sem_Secure()
    {
        var opcoes = SessaoHttp.MontarOpcoes(ContextoCom(https: false), Expira);

        Assert.Equal(SameSiteMode.Lax, opcoes.SameSite);
        Assert.False(opcoes.Secure);
    }

    /// <summary>
    /// O que não muda entre os dois modelos: o cookie é sempre `HttpOnly` — fora
    /// do alcance do JavaScript, que é a razão de ele existir — e preso ao
    /// caminho da sessão.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void O_cookie_e_sempre_HttpOnly_e_preso_ao_caminho_da_sessao(bool https)
    {
        var opcoes = SessaoHttp.MontarOpcoes(ContextoCom(https), Expira);

        Assert.True(opcoes.HttpOnly);
        Assert.Equal(SessaoHttp.CaminhoDaSessao, opcoes.Path);
        Assert.True(opcoes.IsEssential);
    }
}
