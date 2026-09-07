using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CentralAntifraude.Api.Identidade;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Security Gate 11 — hardening da superficie construida.
///
/// **Esta fase nao acrescenta capacidade; ela fecha o que sobrou.** Os gates
/// anteriores testaram cada fase quando ela nasceu. O que este verifica sao as
/// tres coisas que so aparecem olhando o conjunto:
///
/// 1. **o modelo de deploy** — a Fase 14 coloca o frontend em um dominio e a
///    API em outro, e CORS, cookie e Origin precisam concordar ANTES disso;
/// 2. **os limites que faltavam** — refresh e emissao de credencial estao
///    nomeados no `CLAUDE.md` secao 55 e nao tinham limite proprio;
/// 3. **o teto de entrada** — nenhuma rota humana precisa dos 30 MB que o
///    Kestrel aceita por padrao.
///
/// Autorizacao por rota fica em <c>MatrizDeAutorizacaoTests</c> e isolamento
/// entre tenants em <c>IsolamentoCompletoTests</c>: sao varreduras exaustivas,
/// e caberiam mal aqui no meio.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class SecurityGate11Tests : IAsyncLifetime
{
    private const string OrigemPermitida = "https://central-antifraude.vercel.app";
    private const string OrigemEstranha = "https://sitemalicioso.example";

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;

    private readonly Dictionary<PerfilDeUsuario, string> _tokens = [];

    public SecurityGate11Tests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"sg11-{Guid.NewGuid():N}"[..14],
            Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();
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
    // Modelo de deploy: CORS, Origin e cookie
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Sem_origens_declaradas_nenhuma_origem_cruzada_e_autorizada()
    {
        // E o estado de desenvolvimento: o `vite dev` faz proxy e tudo parece
        // mesma origem. Ligar CORS ali criaria uma configuracao que ninguem
        // exercita — e um curinga que ninguem revisa.
        using var requisicao = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/health/live", UriKind.Relative));

        requisicao.Headers.TryAddWithoutValidation("Origin", OrigemPermitida);

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        Assert.False(resposta.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_origem_declarada_e_autorizada_e_a_estranha_nao()
    {
        // **Este e o teste que o ROADMAP 11.4 pede**: validar CORS no modelo de
        // deploy planejado, antes de descobrir cross-origin em producao.
        await using var comCors = CriarComCors();
        using var cliente = comCors.CriarClienteSemCookieAutomatico();

        var permitida = await PreflightAsync(cliente, OrigemPermitida);
        var estranha = await PreflightAsync(cliente, OrigemEstranha);

        Assert.Equal(OrigemPermitida, permitida.Origem);

        // Credencial atravessa: sem isto o navegador nao envia o cookie de
        // refresh e a sessao morre no primeiro F5 do deploy cross-site.
        Assert.Equal("true", permitida.Credenciais);

        // E o navegador precisa conseguir LER a correlacao, ou o frontend nao
        // teria como mostrar o codigo que o suporte pede. `Expose-Headers` vem
        // na resposta REAL, e nao no preflight — por isso a segunda chamada.
        using var real = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/health/live", UriKind.Relative));

        real.Headers.TryAddWithoutValidation("Origin", OrigemPermitida);

        using var respostaReal = await cliente.SendAsync(real, Cancelamento);

        Assert.Contains(
            "X-Correlation-Id",
            Cabecalho(respostaReal, "Access-Control-Expose-Headers") ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        // A origem estranha nao recebe autorizacao nenhuma. Sem o cabecalho, o
        // navegador descarta a resposta.
        Assert.Null(estranha.Origem);
    }

    [Fact]
    public async Task Curinga_de_origem_nunca_aparece_na_resposta()
    {
        // `AllowAnyOrigin` com credencial e proibido pela especificacao, e o
        // motivo e este: permitir credencial de qualquer origem entrega a
        // sessao a qualquer site.
        await using var comCors = CriarComCors();
        using var cliente = comCors.CriarClienteSemCookieAutomatico();

        var resultado = await PreflightAsync(cliente, OrigemPermitida);

        Assert.NotEqual("*", resultado.Origem);
    }

    [Fact]
    public async Task Origem_estranha_continua_recusada_no_refresh_mesmo_com_cors_ligado()
    {
        // CORS e o navegador cooperando; a verificacao de Origin e o servidor
        // recusando. As duas camadas usam a MESMA lista, e a segunda vale
        // mesmo para um cliente que ignore a primeira.
        await using var comCors = CriarComCors();
        using var cliente = comCors.CriarClienteSemCookieAutomatico();

        using var requisicao = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/auth/refresh", UriKind.Relative));

        requisicao.Headers.TryAddWithoutValidation("Origin", OrigemEstranha);

        using var resposta = await cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Cabecalhos de resposta
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/api/transacoes")]
    [InlineData("/api/rota-que-nao-existe")]
    public async Task Os_cabecalhos_de_seguranca_valem_ate_nas_respostas_de_erro(string caminho)
    {
        // De propriedade: `nosniff` numa resposta 404 importa tanto quanto numa
        // 200 — o corpo de erro tambem e conteudo que o navegador poderia
        // tentar adivinhar.
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, new Uri(caminho, UriKind.Relative));
        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal("nosniff", Cabecalho(resposta, "X-Content-Type-Options"));
        Assert.Equal("DENY", Cabecalho(resposta, "X-Frame-Options"));
        Assert.Equal("no-referrer", Cabecalho(resposta, "Referrer-Policy"));
    }

    // -----------------------------------------------------------------------
    // Teto de entrada
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Corpo_gigante_e_recusado_antes_de_qualquer_validacao()
    {
        // Sem o teto global, 30 MB seriam lidos inteiros para depois virar um
        // erro de validacao de campo. O limite recusa na porta.
        var token = await TokenAsync(PerfilDeUsuario.AnalistaDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Post,
            "/api/casos",
            token);

        requisicao.Content = new StringContent(
            "{\"titulo\":\"" + new string('a', (int)SessaoHttp.TamanhoMaximoDoCorpo + 1_000) + "\"}",
            Encoding.UTF8,
            "application/json");

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resposta.StatusCode);
    }

    [Fact]
    public async Task Corpo_dentro_do_teto_chega_a_validacao_normalmente()
    {
        // O teto nao pode ser tao apertado que recuse entrada legitima: uma
        // nota de investigacao tem ate 4.000 caracteres.
        var token = await TokenAsync(PerfilDeUsuario.AnalistaDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Post,
            "/api/casos",
            token);

        requisicao.Content = JsonContent.Create(new
        {
            titulo = new string('a', 4_000),
            alertasIds = Array.Empty<Guid>(),
        });

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        // Recusado pela validacao de dominio, e nao pelo tamanho: e a prova de
        // que o corpo chegou inteiro ate a regra de negocio.
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Limites que faltavam (CLAUDE.md secao 55)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_refresh_tem_limite_proprio()
    {
        // Renovar e anonimo por natureza: a prova de identidade e o cookie.
        // Sem limite, a rota vira um oraculo para adivinhar valores de refresh
        // token.
        await using var isolada = new FabricaDaApi(_banco.StringDeConexao);
        using var cliente = isolada.CriarClienteSemCookieAutomatico();

        var respostas = new List<HttpStatusCode>();

        for (var i = 0; i < 70; i++)
        {
            using var requisicao = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri("/api/auth/refresh", UriKind.Relative));

            using var resposta = await cliente.SendAsync(requisicao, Cancelamento);
            respostas.Add(resposta.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, respostas);

        // E as primeiras passaram: um limite que recusasse desde a primeira
        // chamada quebraria a renovacao legitima de duas abas abertas.
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, respostas.Take(10));
    }

    [Fact]
    public async Task A_emissao_de_credencial_tem_limite_proprio()
    {
        // E a operacao administrativa mais sensivel do produto: cada chamada
        // devolve um segredo novo.
        await using var isolada = new FabricaDaApi(_banco.StringDeConexao);
        using var cliente = isolada.CriarClienteSemCookieAutomatico();

        var integracao = await CenarioDeIngestao.CriarIntegracaoAsync(
            cliente,
            _tenant,
            Cancelamento);

        var token = await CenarioDeIngestao.TokenDeAsync(
            cliente,
            _tenant,
            PerfilDeUsuario.Administrador,
            Cancelamento);

        var respostas = new List<HttpStatusCode>();

        for (var i = 0; i < 25; i++)
        {
            using var requisicao = CenarioDeIdentidade.Autenticada(
                HttpMethod.Post,
                $"/api/integracoes/{integracao.Id}/credenciais",
                token);

            using var resposta = await cliente.SendAsync(requisicao, Cancelamento);
            respostas.Add(resposta.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, respostas);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, respostas.Take(5));
    }

    [Fact]
    public async Task O_limite_de_credencial_e_por_organizacao_e_nao_global()
    {
        // Um limite global faria a conta comprometida de um cliente travar a
        // operacao de todos os outros — negacao de servico entre tenants pela
        // porta da frente.
        await using var isolada = new FabricaDaApi(_banco.StringDeConexao);
        using var cliente = isolada.CriarClienteSemCookieAutomatico();

        var outro = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"sg11b-{Guid.NewGuid():N}"[..13],
            Cancelamento);

        var integracaoA = await CenarioDeIngestao.CriarIntegracaoAsync(cliente, _tenant, Cancelamento);
        var integracaoB = await CenarioDeIngestao.CriarIntegracaoAsync(cliente, outro, Cancelamento);

        var tokenA = await CenarioDeIngestao.TokenDeAsync(
            cliente,
            _tenant,
            PerfilDeUsuario.Administrador,
            Cancelamento);

        var tokenB = await CenarioDeIngestao.TokenDeAsync(
            cliente,
            outro,
            PerfilDeUsuario.Administrador,
            Cancelamento);

        // A esgota a propria cota.
        for (var i = 0; i < 25; i++)
        {
            using var requisicao = CenarioDeIdentidade.Autenticada(
                HttpMethod.Post,
                $"/api/integracoes/{integracaoA.Id}/credenciais",
                tokenA);

            using var _ = await cliente.SendAsync(requisicao, Cancelamento);
        }

        using var doB = CenarioDeIdentidade.Autenticada(
            HttpMethod.Post,
            $"/api/integracoes/{integracaoB.Id}/credenciais",
            tokenB);

        using var resposta = await cliente.SendAsync(doB, Cancelamento);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Autenticacao
    // -----------------------------------------------------------------------

    [Fact]
    public void O_access_token_e_curto_e_sem_tolerancia_de_relogio()
    {
        // A tolerancia padrao da biblioteca e de 5 minutos, o que estenderia
        // na pratica um token de 15 para 20. Emissor e validador aqui sao o
        // mesmo processo: nao ha relogios distintos para conciliar.
        var opcoes = new OpcoesDeAutenticacao();

        Assert.True(opcoes.MinutosDoAccessToken <= 15);

        var parametros = CentralAntifraude.Infrastructure.Identidade.EmissorDeAccessToken
            .MontarParametrosDeValidacao(new OpcoesDeAutenticacao
            {
                ChaveDeAssinatura = Convert.ToBase64String(new byte[48]),
            });

        Assert.Equal(TimeSpan.Zero, parametros.ClockSkew);
        Assert.True(parametros.ValidateLifetime);
        Assert.True(parametros.ValidateIssuer);
        Assert.True(parametros.ValidateAudience);
        Assert.True(parametros.ValidateIssuerSigningKey);

        // Sem a lista de algoritmos, um token com "alg": "none" ou assinado
        // com outro algoritmo poderia ser aceito.
        Assert.Equal(["HS256"], parametros.ValidAlgorithms);
    }

    // -----------------------------------------------------------------------
    // Entrada
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("/api/casos", "{\"titulo\":\"ok\",\"alertasIds\":[],\"organizacaoId\":\"x\"}")]
    [InlineData("/api/casos", "{\"titulo\":\"ok\",\"alertasIds\":[],\"status\":\"Resolvido\"}")]
    [InlineData("/api/backtests", "{\"inicio\":\"2026-01-01T00:00:00Z\",\"fim\":\"2026-01-02T00:00:00Z\",\"candidato\":{}}")]
    public async Task Campo_desconhecido_no_corpo_e_recusado_em_toda_rota(string caminho, string corpo)
    {
        // JSON estrito vale para o produto inteiro, e nao rota a rota: quem
        // escreve "amout" no lugar de "amount" precisa descobrir na primeira
        // chamada.
        var token = await TokenAsync(PerfilDeUsuario.Administrador);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Post, caminho, token);
        requisicao.Content = new StringContent(corpo, Encoding.UTF8, "application/json");

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Byte_nulo_e_escape_de_terminal_sao_recusados_em_texto_humano()
    {
        // Quebra de linha e tabulacao passam — uma nota tem paragrafos. O
        // resto nao vem de um teclado e nao entra no banco.
        var token = await TokenAsync(PerfilDeUsuario.AnalistaDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Post, "/api/casos", token);

        requisicao.Content = new StringContent(
            "{\"titulo\":\"caso\\u0000malicioso\",\"alertasIds\":[]}",
            Encoding.UTF8,
            "application/json");

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Nenhuma_resposta_de_erro_revela_a_pilha_ou_o_banco()
    {
        // O ambiente do host de teste e Production de proposito: e o contrato
        // de producao que precisa ser verificado.
        var token = await TokenAsync(PerfilDeUsuario.AnalistaDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Get,
            $"/api/casos/{Guid.CreateVersion7()}",
            token);

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);
        var corpo = await resposta.Content.ReadAsStringAsync(Cancelamento);

        foreach (var proibido in new[]
        {
            "Npgsql",
            "EntityFrameworkCore",
            "StackTrace",
            "at CentralAntifraude",
            "Host=",
            "Password=",
        })
        {
            Assert.DoesNotContain(proibido, corpo, StringComparison.OrdinalIgnoreCase);
        }
    }

    // =======================================================================
    // Apoio
    // =======================================================================

    private FabricaDaApi CriarComCors() =>
        new(
            _banco.StringDeConexao,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [$"{OpcoesDeAutenticacao.Secao}:OrigensPermitidas:0"] = OrigemPermitida,
            });

    private sealed record RespostaDePreflight(string? Origem, string? Credenciais, string? Expostos);

    private static async Task<RespostaDePreflight> PreflightAsync(HttpClient cliente, string origem)
    {
        using var requisicao = new HttpRequestMessage(
            HttpMethod.Options,
            new Uri("/api/transacoes", UriKind.Relative));

        requisicao.Headers.TryAddWithoutValidation("Origin", origem);
        requisicao.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
        requisicao.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "Authorization");

        using var resposta = await cliente.SendAsync(requisicao, Cancelamento);

        return new RespostaDePreflight(
            Cabecalho(resposta, "Access-Control-Allow-Origin"),
            Cabecalho(resposta, "Access-Control-Allow-Credentials"),
            Cabecalho(resposta, "Access-Control-Expose-Headers"));
    }

    private static string? Cabecalho(HttpResponseMessage resposta, string nome) =>
        resposta.Headers.TryGetValues(nome, out var valores) ? valores.FirstOrDefault() : null;

    private async Task<string> TokenAsync(PerfilDeUsuario perfil)
    {
        if (_tokens.TryGetValue(perfil, out var existente))
        {
            return existente;
        }

        var token = await CenarioDeIngestao.TokenDeAsync(_cliente, _tenant, perfil, Cancelamento);
        _tokens[perfil] = token;

        return token;
    }
}
