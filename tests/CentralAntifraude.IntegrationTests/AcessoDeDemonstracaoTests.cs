using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// O acesso de demonstracao sem senha.
///
/// **O que ele resolve, e o que ele nao pode virar.** Um visitante precisa
/// conhecer o produto sem ter conta — mas "entrar sem senha" e, mal desenhado,
/// uma porta dos fundos. As tres travas que estes testes prendem:
///
/// 1. So funciona quando o AMBIENTE liga a demo. Desligada (o padrao), o
///    endpoint responde 404, sem admitir que existe.
/// 2. Quem escolhe a conta e o SERVIDOR. O cliente nao manda e-mail, entao o
///    botao nao pode ser reaproveitado para entrar em qualquer conta.
/// 3. A conta e um Analista — o perfil operacional. Um visitante nunca entra
///    como Administrador.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class AcessoDeDemonstracaoTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private TenantDeTeste _tenant = null!;

    public AcessoDeDemonstracaoTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        // O tenant de teste cria os quatro perfis com e-mails `<perfil>@<codigo>`.
        // A conta de demonstracao aqui aponta para o Analista desse tenant.
        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"demo-{Guid.NewGuid():N}"[..12],
            Cancelamento);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private FabricaDaApi ComDemo(bool habilitada) =>
        new(
            _banco.StringDeConexao,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [$"{OpcoesDeAutenticacao.Secao}:DemoHabilitado"] = habilitada ? "true" : "false",
                [$"{OpcoesDeAutenticacao.Secao}:EmailDaContaDemo"] =
                    _tenant.EmailDe(PerfilDeUsuario.AnalistaDeFraude),
            });

    /// <summary>
    /// Com a demo ligada: emite sessao real, e a sessao e de um Analista.
    /// </summary>
    [Fact]
    public async Task Demo_habilitada_emite_sessao_de_analista()
    {
        await using var fabrica = ComDemo(habilitada: true);
        using var cliente = fabrica.CriarClienteSemCookieAutomatico();

        using var resposta = await cliente.PostAsync("/api/auth/demo", content: null, Cancelamento);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var corpo = await resposta.Content.ReadAsStringAsync(Cancelamento);
        using var json = JsonDocument.Parse(corpo);

        // Access token no corpo, refresh token no cookie httponly — igual ao login.
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("accessToken").GetString()));
        Assert.Equal(
            nameof(PerfilDeUsuario.AnalistaDeFraude),
            json.RootElement.GetProperty("usuario").GetProperty("perfil").GetString());

        Assert.True(resposta.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(cookies!, c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A sessao de demonstracao e USAVEL: o access token abre uma rota protegida.
    /// Emitir um token que nao vale nada seria pior do que nao emitir.
    /// </summary>
    [Fact]
    public async Task A_sessao_de_demonstracao_acessa_rota_protegida()
    {
        await using var fabrica = ComDemo(habilitada: true);
        using var cliente = fabrica.CriarClienteSemCookieAutomatico();

        using var entrada = await cliente.PostAsync("/api/auth/demo", content: null, Cancelamento);
        var corpo = await entrada.Content.ReadAsStringAsync(Cancelamento);
        using var json = JsonDocument.Parse(corpo);
        var token = json.RootElement.GetProperty("accessToken").GetString()!;

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, "/api/alertas", token);
        using var alertas = await cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.OK, alertas.StatusCode);
    }

    /// <summary>
    /// Com a demo DESLIGADA (o padrao de producao real): 404, sem se anunciar.
    /// </summary>
    [Fact]
    public async Task Demo_desabilitada_responde_como_se_o_endpoint_nao_existisse()
    {
        await using var fabrica = ComDemo(habilitada: false);
        using var cliente = fabrica.CriarClienteSemCookieAutomatico();

        using var resposta = await cliente.PostAsync("/api/auth/demo", content: null, Cancelamento);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.False(resposta.Headers.Contains("Set-Cookie"));
    }
}
