using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Security Gate 2 (ROADMAP secao 2.10), item por item, contra a API real.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class SecurityGate2Tests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;

    public SecurityGate2Tests(FixtureDoBanco banco) => _banco = banco;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"gate2-{Guid.NewGuid():N}"[..14],
            TestContext.Current.CancellationToken);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();

        _integracao = await CenarioDeIngestao.CriarIntegracaoAsync(
            _cliente,
            _tenant,
            TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _cliente?.Dispose();
        if (_fabrica is not null)
        {
            await _fabrica.DisposeAsync();
        }
    }

    /// <summary>
    /// Envia com a credencial padrao do cenario. Para exercitar credencial
    /// ausente ou invalida use <see cref="EnviarComChaveAsync"/> - aqui um
    /// null viraria a chave valida e o teste passaria pelo motivo errado.
    /// </summary>
    private Task<HttpResponseMessage> EnviarAsync(
        object corpo,
        string chaveDeIdempotencia = "gate2-chave-001") =>
        EnviarComChaveAsync(_integracao.Chave, corpo, chaveDeIdempotencia);

    private Task<HttpResponseMessage> EnviarComChaveAsync(
        string? chaveDaApi,
        object corpo,
        string chaveDeIdempotencia = "gate2-chave-001") =>
        CenarioDeIngestao.EnviarAsync(
            _cliente,
            chaveDaApi,
            chaveDeIdempotencia,
            corpo,
            TestContext.Current.CancellationToken);

    private Task<string> TokenDeAsync(PerfilDeUsuario perfil) =>
        CenarioDeIngestao.TokenDeAsync(_cliente, _tenant, perfil, TestContext.Current.CancellationToken);

    // -----------------------------------------------------------------------
    // Credencial da integracao
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("caf_0000000000000000_segredo-inexistente")]
    [InlineData("caf_formato-errado")]
    [InlineData("nao-e-uma-chave")]
    public async Task Credencial_invalida_ou_ausente_recebe_401(string? chave)
    {
        var resposta = await EnviarComChaveAsync(chave, CenarioDeIngestao.Corpo());

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Toda_falha_de_credencial_devolve_a_mesma_resposta()
    {
        // Distinguir "chave inexistente" de "segredo errado" daria a quem
        // testa chaves um mapa do que existe.
        var inexistente = await EnviarComChaveAsync(
            "caf_0000000000000000_qualquer",
            CenarioDeIngestao.Corpo());

        var segredoErrado = await EnviarComChaveAsync(
            $"caf_{_integracao.IdentificadorPublico}_segredo-errado",
            CenarioDeIngestao.Corpo());

        Assert.Equal(inexistente.StatusCode, segredoErrado.StatusCode);

        // A comparacao ignora traceId e idDeCorrelacao: os dois sao unicos por
        // requisicao POR DESENHO, e e assim que o suporte separa um chamado do
        // outro. O que precisa ser identico e tudo o mais.
        Assert.Equal(
            await CorpoSemIdentificadoresUnicosAsync(inexistente),
            await CorpoSemIdentificadoresUnicosAsync(segredoErrado));
    }

    private static async Task<string> CorpoSemIdentificadoresUnicosAsync(HttpResponseMessage resposta)
    {
        using var json = JsonDocument.Parse(
            await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var campos = json.RootElement
            .EnumerateObject()
            .Where(p => p.Name is not ("traceId" or "idDeCorrelacao"))
            .Select(p => $"{p.Name}={p.Value}")
            .Order(StringComparer.Ordinal);

        return string.Join("|", campos);
    }

    [Fact]
    public async Task Credencial_revogada_para_de_funcionar_na_hora()
    {
        var token = await TokenDeAsync(PerfilDeUsuario.Administrador);

        var detalhe = await _cliente.SendAsync(
            CenarioDeIdentidade.Autenticada(HttpMethod.Get, $"/api/integracoes/{_integracao.Id}", token),
            TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(
            await detalhe.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var credencialId = json.RootElement.GetProperty("credenciais")[0].GetProperty("id").GetGuid();

        using var revogacao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Delete,
            $"/api/integracoes/{_integracao.Id}/credenciais/{credencialId}",
            token);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await _cliente.SendAsync(revogacao, TestContext.Current.CancellationToken)).StatusCode);

        var apos = await EnviarAsync(CenarioDeIngestao.Corpo(), "gate2-revog-01");

        Assert.Equal(HttpStatusCode.Unauthorized, apos.StatusCode);
    }

    [Fact]
    public async Task Desativar_a_integracao_invalida_as_credenciais_dela()
    {
        // Sem isto, "desativar" seria um rotulo na tela: quem tem a chave
        // continuaria enviando transacoes.
        var token = await TokenDeAsync(PerfilDeUsuario.Administrador);

        using var desativacao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Put,
            $"/api/integracoes/{_integracao.Id}/ativacao",
            token);
        desativacao.Content = JsonContent.Create(new { ativa = false });

        Assert.Equal(
            HttpStatusCode.OK,
            (await _cliente.SendAsync(desativacao, TestContext.Current.CancellationToken)).StatusCode);

        var apos = await EnviarAsync(CenarioDeIngestao.Corpo(), "gate2-desat-01");

        Assert.Equal(HttpStatusCode.Unauthorized, apos.StatusCode);
    }

    [Fact]
    public async Task Rotacao_mantem_as_duas_credenciais_validas()
    {
        // E o que permite trocar a chave sem derrubar a ingestao do cliente.
        var token = await TokenDeAsync(PerfilDeUsuario.Administrador);

        using var rotacao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Post,
            $"/api/integracoes/{_integracao.Id}/credenciais",
            token);

        var resposta = await _cliente.SendAsync(rotacao, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        using var json = JsonDocument.Parse(
            await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var novaChave = json.RootElement.GetProperty("chave").GetString();

        var comAntiga = await EnviarAsync(
            CenarioDeIngestao.Corpo(identificadorExterno: "rot-a"), "gate2-rot-01");
        var comNova = await EnviarComChaveAsync(
            novaChave, CenarioDeIngestao.Corpo(identificadorExterno: "rot-b"), "gate2-rot-02");

        Assert.Equal(HttpStatusCode.Created, comAntiga.StatusCode);
        Assert.Equal(HttpStatusCode.Created, comNova.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Separacao entre humano e maquina
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Token_humano_nao_serve_na_ingestao()
    {
        // CLAUDE.md secao 50: integracoes nao fingem ser usuarios, e usuarios
        // nao entram pela porta das integracoes.
        var token = await TokenDeAsync(PerfilDeUsuario.Administrador);

        using var requisicao = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(CenarioDeIngestao.CaminhoDaIngestao, UriKind.Relative))
        {
            Content = JsonContent.Create(CenarioDeIngestao.Corpo()),
        };
        requisicao.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        requisicao.Headers.TryAddWithoutValidation(CenarioDeIngestao.CabecalhoDeIdempotencia, "gate2-hum-01");

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Credencial_de_integracao_nao_serve_nas_rotas_humanas()
    {
        using var requisicao = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/api/usuarios", UriKind.Relative));
        requisicao.Headers.TryAddWithoutValidation("Authorization", $"ApiKey {_integracao.Chave}");

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Theory]
    [InlineData(PerfilDeUsuario.AnalistaDeFraude)]
    [InlineData(PerfilDeUsuario.Auditor)]
    [InlineData(PerfilDeUsuario.SupervisorDeFraude)]
    public async Task Somente_administrador_gerencia_integracoes(PerfilDeUsuario perfil)
    {
        var token = await TokenDeAsync(perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, "/api/integracoes", token);
        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Contrato de entrada
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Tenant_injection_pelo_payload_e_recusado()
    {
        // O tenant vem da credencial. Um "organizacaoId" no corpo e apenas um
        // campo desconhecido - e o JSON estrito recusa a requisicao inteira.
        using var requisicao = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(CenarioDeIngestao.CaminhoDaIngestao, UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                identificadorExterno = "inj-0001",
                valor = 10m,
                moeda = "BRL",
                ocorridaEm = DateTimeOffset.UtcNow.AddMinutes(-1),
                clienteExternoId = "cli-1",
                referenciaDoInstrumento = "pi_1",
                organizacaoId = Guid.NewGuid(),
            }),
        };
        requisicao.Headers.TryAddWithoutValidation("Authorization", $"ApiKey {_integracao.Chave}");
        requisicao.Headers.TryAddWithoutValidation(CenarioDeIngestao.CabecalhoDeIdempotencia, "gate2-inj-01");

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("requisicao_malformada", await CenarioDeIngestao.CodigoDoErroAsync(resposta));
    }

    [Fact]
    public async Task A_transacao_gravada_pertence_ao_tenant_da_credencial()
    {
        var resposta = await EnviarAsync(
            CenarioDeIngestao.Corpo(identificadorExterno: "tenant-0001"),
            "gate2-tenant-01");

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var transacao = await contexto.Transacoes.FirstAsync(
            t => t.IdentificadorExterno == "tenant-0001",
            TestContext.Current.CancellationToken);

        Assert.Equal(_tenant.OrganizacaoId, transacao.OrganizacaoId);
        Assert.Equal(_integracao.Id, transacao.IntegracaoId);
    }

    [Fact]
    public async Task Sem_chave_de_idempotencia_a_requisicao_e_recusada()
    {
        var resposta = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracao.Chave,
            chaveDeIdempotencia: null,
            CenarioDeIngestao.Corpo(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Valor_nao_positivo_e_recusado(decimal valor)
    {
        var resposta = await EnviarAsync(
            CenarioDeIngestao.Corpo(identificadorExterno: "val-0001", valor: valor),
            "gate2-val-01");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Theory]
    [InlineData("REAL")]
    [InlineData("XX")]
    [InlineData("")]
    public async Task Moeda_invalida_e_recusada(string moeda)
    {
        var resposta = await EnviarAsync(
            CenarioDeIngestao.Corpo(identificadorExterno: "moe-0001", moeda: moeda),
            "gate2-moe-01");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Data_muito_no_futuro_e_recusada()
    {
        var resposta = await EnviarAsync(
            CenarioDeIngestao.Corpo(
                identificadorExterno: "dat-0001",
                ocorridaEm: DateTimeOffset.UtcNow.AddDays(1)),
            "gate2-dat-01");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Numero_de_cartao_em_campo_de_texto_e_recusado()
    {
        // A plataforma nao aceita dado completo de cartao (CLAUDE.md secoes
        // 56 e 58). O contrato nao tem campo para isso, e esta e a barreira
        // contra o acidente de colar o PAN num campo de identificador.
        var resposta = await EnviarAsync(
            CenarioDeIngestao.Corpo(
                identificadorExterno: "pan-0001",
                referenciaDoInstrumento: "4111111111111111"),
            "gate2-pan-01");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);

        var texto = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("cartao", texto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Payload_grande_demais_e_recusado()
    {
        var resposta = await EnviarAsync(
            CenarioDeIngestao.Corpo(
                identificadorExterno: "big-0001",
                fingerprintDoDispositivo: new string('a', 64 * 1024)),
            "gate2-big-01");

        // 400 pela validacao de tamanho de campo, ou 413 pelo teto do corpo:
        // qualquer um serve, desde que a requisicao NAO seja aceita.
        Assert.True(
            resposta.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
            $"Payload gigante deveria ser recusado, veio {(int)resposta.StatusCode}.");
    }

    // -----------------------------------------------------------------------
    // Minimizacao de dados
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_endereco_ip_nao_e_persistido_em_lugar_nenhum()
    {
        // CLAUDE.md secao 57: o que fica e o HMAC, nunca o endereco. O
        // fingerprint responde "mesmo lugar?" sem guardar quem.
        const string ip = "203.0.113.77";

        await EnviarAsync(
            CenarioDeIngestao.Corpo(identificadorExterno: "ip-0001", enderecoIp: ip),
            "gate2-ip-01");

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var transacao = await contexto.Transacoes.FirstAsync(
            t => t.IdentificadorExterno == "ip-0001",
            TestContext.Current.CancellationToken);

        Assert.NotNull(transacao.FingerprintDoIp);
        Assert.DoesNotContain(ip, transacao.FingerprintDoIp, StringComparison.Ordinal);
        Assert.Equal(64, transacao.FingerprintDoIp!.Length);
    }

    [Fact]
    public async Task O_mesmo_ip_produz_o_mesmo_fingerprint_entre_transacoes()
    {
        // E o que uma regra de rede vai precisar na Fase 3.
        const string ip = "203.0.113.88";

        await EnviarAsync(CenarioDeIngestao.Corpo(identificadorExterno: "ip-0002", enderecoIp: ip), "gate2-ip-02");
        await EnviarAsync(CenarioDeIngestao.Corpo(identificadorExterno: "ip-0003", enderecoIp: ip), "gate2-ip-03");

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var fingerprints = await contexto.Transacoes
            .Where(t => t.IdentificadorExterno == "ip-0002" || t.IdentificadorExterno == "ip-0003")
            .Select(t => t.FingerprintDoIp)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, fingerprints.Count);
        Assert.Single(fingerprints.Distinct());
    }

    // -----------------------------------------------------------------------
    // Segredo em log e em resposta
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_chave_da_api_nao_aparece_no_log()
    {
        await EnviarAsync(CenarioDeIngestao.Corpo(identificadorExterno: "log-0001"), "gate2-log-01");

        var segredo = _integracao.Chave.Split('_', 3)[2];

        Assert.DoesNotContain(
            _fabrica.Registros,
            registro => registro.Contains(segredo, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_credencial_nao_e_exibida_de_novo_apos_a_criacao()
    {
        // So o hash fica no banco. Se a chave se perder, o caminho e
        // rotacionar - nao ha endpoint que a mostre outra vez.
        var token = await TokenDeAsync(PerfilDeUsuario.Administrador);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Get,
            $"/api/integracoes/{_integracao.Id}",
            token);

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);
        var texto = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var segredo = _integracao.Chave.Split('_', 3)[2];

        Assert.DoesNotContain(segredo, texto, StringComparison.Ordinal);
        Assert.DoesNotContain("hashDoSegredo", texto, StringComparison.OrdinalIgnoreCase);

        // O identificador publico APARECE, e deve: e como o administrador
        // reconhece qual credencial esta revogando.
        Assert.Contains(_integracao.IdentificadorPublico, texto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_auditoria_registra_a_gestao_de_integracoes_sem_o_segredo()
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var registros = await contexto.RegistrosDeAuditoria
            .Where(r => r.Entidade == "Integracao")
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(registros, r => r.Operacao == OperacaoAuditada.IntegracaoCriada);

        var segredo = _integracao.Chave.Split('_', 3)[2];

        foreach (var registro in registros)
        {
            var conteudo = $"{registro.Detalhe} {registro.AutorDescricao}";

            Assert.DoesNotContain(segredo, conteudo, StringComparison.Ordinal);
            Assert.DoesNotContain("caf_", conteudo, StringComparison.Ordinal);
        }
    }
}
