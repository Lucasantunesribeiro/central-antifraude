using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Domain.Identidade;

namespace CentralAntifraude.IntegrationTests.Infra;

/// <summary>Uma integracao criada pela API, com a chave que ela devolveu.</summary>
public sealed record IntegracaoDeTeste(Guid Id, string Chave)
{
    /// <summary>Parte publica da chave — a que aparece na listagem.</summary>
    public string IdentificadorPublico => Chave.Split('_')[1];
}

/// <summary>
/// Atalhos para os testes de ingestao: criar integracao, montar corpo e
/// enviar transacao.
/// </summary>
public static class CenarioDeIngestao
{
    public const string CaminhoDaIngestao = "/api/ingestao/transacoes";
    public const string CabecalhoDeIdempotencia = "Idempotency-Key";
    public const string EsquemaDaChave = "ApiKey";

    /// <summary>Cria uma integracao como Administrador e devolve a chave emitida.</summary>
    public static async Task<IntegracaoDeTeste> CriarIntegracaoAsync(
        HttpClient cliente,
        TenantDeTeste tenant,
        CancellationToken cancellationToken,
        string nome = "Integracao de teste")
    {
        var (token, _) = await CenarioDeIdentidade.EntrarAsync(
            cliente,
            tenant.EmailDe(PerfilDeUsuario.Administrador),
            cancellationToken);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Post,
            "/api/integracoes",
            token);
        requisicao.Content = JsonContent.Create(new { nome });

        var resposta = await cliente.SendAsync(requisicao, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(
            await resposta.Content.ReadAsStringAsync(cancellationToken));

        return new IntegracaoDeTeste(
            json.RootElement.GetProperty("integracao").GetProperty("id").GetGuid(),
            json.RootElement.GetProperty("credencial").GetProperty("chave").GetString()!);
    }

    /// <summary>Token de acesso de um perfil do tenant.</summary>
    public static async Task<string> TokenDeAsync(
        HttpClient cliente,
        TenantDeTeste tenant,
        PerfilDeUsuario perfil,
        CancellationToken cancellationToken)
    {
        var (token, _) = await CenarioDeIdentidade.EntrarAsync(
            cliente,
            tenant.EmailDe(perfil),
            cancellationToken);

        return token;
    }

    /// <summary>
    /// Corpo valido de uma tentativa de pagamento. Cada parametro tem um
    /// padrao razoavel para que cada teste sobrescreva so o que exercita.
    /// </summary>
    public static object Corpo(
        string identificadorExterno = "pedido-1001",
        decimal valor = 249.90m,
        string moeda = "BRL",
        DateTimeOffset? ocorridaEm = null,
        string clienteExternoId = "cli-777",
        string referenciaDoInstrumento = "pi_demo_123",
        string? fingerprintDoDispositivo = "disp-abc",
        string? enderecoIp = "203.0.113.10",
        string? paisDeOrigem = "BR") =>
        new
        {
            identificadorExterno,
            valor,
            moeda,
            ocorridaEm = ocorridaEm ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            clienteExternoId,
            referenciaDoInstrumento,
            fingerprintDoDispositivo,
            enderecoIp,
            paisDeOrigem,
        };

    /// <summary>Envia uma transacao com a chave e a idempotencia informadas.</summary>
    public static Task<HttpResponseMessage> EnviarAsync(
        HttpClient cliente,
        string? chaveDaApi,
        string? chaveDeIdempotencia,
        object corpo,
        CancellationToken cancellationToken)
    {
        var requisicao = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(CaminhoDaIngestao, UriKind.Relative))
        {
            Content = JsonContent.Create(corpo),
        };

        if (chaveDaApi is not null)
        {
            requisicao.Headers.TryAddWithoutValidation("Authorization", $"{EsquemaDaChave} {chaveDaApi}");
        }

        if (chaveDeIdempotencia is not null)
        {
            requisicao.Headers.TryAddWithoutValidation(CabecalhoDeIdempotencia, chaveDeIdempotencia);
        }

        return cliente.SendAsync(requisicao, cancellationToken);
    }

    public static async Task<Guid> IdDaTransacaoAsync(HttpResponseMessage resposta) =>
        (await LerCampoAsync(resposta, "id")).GetGuid();

    public static async Task<string?> SituacaoAsync(HttpResponseMessage resposta) =>
        (await LerCampoAsync(resposta, "situacao")).GetString();

    /// <summary>
    /// Le um campo da resposta de ingestao, falhando com diagnostico util
    /// quando ele nao esta la.
    ///
    /// Sem isto, uma resposta inesperada - um 401, por exemplo - viraria um
    /// KeyNotFoundException seco, que esconde o que de fato aconteceu.
    /// </summary>
    private static async Task<JsonElement> LerCampoAsync(HttpResponseMessage resposta, string campo)
    {
        var texto = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(texto);

        if (!json.RootElement.TryGetProperty(campo, out var valor))
        {
            Assert.Fail(
                $"A resposta nao traz '{campo}'. Status {(int)resposta.StatusCode}. Corpo: {texto}");
        }

        return valor.Clone();
    }

    public static async Task<string?> CodigoDoErroAsync(HttpResponseMessage resposta)
    {
        using var json = JsonDocument.Parse(
            await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return json.RootElement.TryGetProperty("codigo", out var codigo) ? codigo.GetString() : null;
    }
}
