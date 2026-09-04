using System.Net;
using System.Text.Json;
using CentralAntifraude.Api.Correlacao;
using CentralAntifraude.IntegrationTests.Infra;
using CentralAntifraude.Infrastructure.Identidade;
using CentralAntifraude.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// A API real, hospedada em memoria, contra o PostgreSQL real do container.
/// Cobre os testes minimos da Fase 0 (ROADMAP secao 0.10): a aplicacao sobe,
/// o health responde e o contrato de erro se comporta.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class ApiTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;

    public ApiTests(FixtureDoBanco banco)
    {
        _banco = banco;
    }

    public async ValueTask InitializeAsync()
    {
        var construtor = new DbContextOptionsBuilder<CentralAntifraudeDbContext>();
        OpcoesDoDbContext.Configurar(construtor, _banco.StringDeConexao);
        await using var contexto = new CentralAntifraudeDbContext(construtor.Options, ContextoDeUsuarioFixo.Anonimo);
        await contexto.Database.MigrateAsync(TestContext.Current.CancellationToken);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _cliente?.Dispose();
        if (_fabrica is not null)
        {
            await _fabrica.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Saude
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Health_live_responde_sem_tocar_no_banco()
    {
        var resposta = await _cliente.GetAsync(
            new Uri("/health/live", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        using var corpo = JsonDocument.Parse(
            await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Healthy", corpo.RootElement.GetProperty("estado").GetString());

        // Liveness nao pode depender do banco: se dependesse, uma queda do
        // PostgreSQL faria o orquestrador reiniciar processos saudaveis em
        // cascata, transformando indisponibilidade parcial em total.
        Assert.Empty(corpo.RootElement.GetProperty("componentes").EnumerateObject());
    }

    [Fact]
    public async Task Health_ready_verifica_o_postgresql_de_verdade()
    {
        var resposta = await _cliente.GetAsync(
            new Uri("/health/ready", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        using var corpo = JsonDocument.Parse(
            await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Healthy", corpo.RootElement.GetProperty("estado").GetString());
        Assert.Equal(
            "Healthy",
            corpo.RootElement.GetProperty("componentes").GetProperty("postgresql").GetString());
    }

    [Fact]
    public async Task Health_ready_fica_indisponivel_quando_o_banco_nao_responde()
    {
        // Sem este teste, o anterior poderia estar passando por engano - um
        // health check que sempre devolve Healthy tambem passaria.
        await using var fabricaSemBanco = new FabricaDaApi(
            "Host=127.0.0.1;Port=1;Database=inexistente;Username=ninguem;Password=nenhuma;Timeout=2");
        using var cliente = fabricaSemBanco.CreateClient();

        var resposta = await cliente.GetAsync(
            new Uri("/health/ready", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);

        var texto = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var corpo = JsonDocument.Parse(texto);
        Assert.Equal("Unhealthy", corpo.RootElement.GetProperty("estado").GetString());

        // E o endpoint de saude nao pode virar um vazamento de topologia.
        Assert.DoesNotContain("127.0.0.1", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("Username", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", texto, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Contrato de erro
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Rota_inexistente_para_anonimo_responde_401_e_nao_404()
    {
        // Desde a Fase 1 existe uma FallbackPolicy que exige autenticacao em
        // tudo que nao declare o contrario. O efeito colateral e desejavel:
        // um anonimo nao consegue mapear quais rotas existem comparando 404
        // com 401.
        var resposta = await _cliente.GetAsync(
            new Uri("/rota-que-nao-existe", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
    }

    // -----------------------------------------------------------------------
    // Correlacao
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Toda_resposta_devolve_o_identificador_de_correlacao()
    {
        var resposta = await _cliente.GetAsync(
            new Uri("/health/live", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.True(resposta.Headers.TryGetValues(
            MiddlewareDeCorrelacao.NomeDoCabecalho,
            out var valores));
        Assert.False(string.IsNullOrWhiteSpace(valores!.Single()));
    }

    [Fact]
    public async Task Identificador_enviado_pelo_cliente_e_preservado_ponta_a_ponta()
    {
        using var requisicao = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/health/live", UriKind.Relative));
        requisicao.Headers.Add(MiddlewareDeCorrelacao.NomeDoCabecalho, "pedido-do-integrador-01");

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(
            "pedido-do-integrador-01",
            resposta.Headers.GetValues(MiddlewareDeCorrelacao.NomeDoCabecalho).Single());
    }

    [Fact]
    public async Task Identificador_malformado_do_cliente_nao_volta_na_resposta()
    {
        using var requisicao = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/health/live", UriKind.Relative));
        requisicao.Headers.TryAddWithoutValidation(
            MiddlewareDeCorrelacao.NomeDoCabecalho,
            "<script>alert(1)</script>");

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        var devolvido = resposta.Headers.GetValues(MiddlewareDeCorrelacao.NomeDoCabecalho).Single();

        Assert.DoesNotContain("script", devolvido, StringComparison.OrdinalIgnoreCase);
        Assert.True(Guid.TryParse(devolvido, out _));
    }

    // -----------------------------------------------------------------------
    // Configuracao
    // -----------------------------------------------------------------------

    [Fact]
    public void Aplicacao_recusa_subir_sem_string_de_conexao()
    {
        // Falha fechada: e melhor nao subir do que subir e falhar em toda
        // avaliacao de risco com o processo respondendo 200 no liveness.
        using var fabricaSemConfiguracao = new FabricaDaApi(string.Empty);

        var excecao = Assert.ThrowsAny<Exception>(() => fabricaSemConfiguracao.CreateClient());

        Assert.Contains("Postgres", excecao.ToString(), StringComparison.Ordinal);
    }
}
