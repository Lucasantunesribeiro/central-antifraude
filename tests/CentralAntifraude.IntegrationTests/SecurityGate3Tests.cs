using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Security Gate 3 (ROADMAP secao 3.10), item por item, contra a API real.
///
/// A pergunta que este arquivo responde e uma so: **alguem de fora consegue
/// influenciar o resultado do motor de risco?** Score, decisao, peso de regra,
/// limiar e configuracao sao produzidos pelo servidor a partir da transacao e
/// do historico. Nenhum deles pode ser escolhido por quem envia o pedido, nem
/// alcancado por quem esta em outro tenant.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class SecurityGate3Tests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenantA = null!;
    private TenantDeTeste _tenantB = null!;
    private IntegracaoDeTeste _integracaoA = null!;

    public SecurityGate3Tests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        var sufixo = $"{Guid.NewGuid():N}"[..8];

        _tenantA = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"g3a-{sufixo}",
            Cancelamento);

        _tenantB = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"g3b-{sufixo}",
            Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();

        _integracaoA = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenantA, Cancelamento);
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
    // O cliente nao escolhe o resultado
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("score")]
    [InlineData("decisao")]
    [InlineData("avaliacao")]
    [InlineData("sinais")]
    [InlineData("versaoDePerfilId")]
    [InlineData("organizacaoId")]
    [InlineData("tenantId")]
    [InlineData("pontos")]
    public async Task Campo_interno_no_payload_de_ingestao_e_recusado(string campo)
    {
        // O contrato de entrada e fechado (JsonUnmappedMemberHandling.Disallow):
        // campo desconhecido vira 400, e nao um campo ignorado em silencio. A
        // diferenca importa - ignorar faria o integrador acreditar que o valor
        // foi aceito.
        var corpo = new Dictionary<string, object?>
        {
            ["identificadorExterno"] = "gate3-massa",
            ["valor"] = 100m,
            ["moeda"] = "BRL",
            ["ocorridaEm"] = DateTimeOffset.UtcNow.AddMinutes(-1),
            ["clienteExternoId"] = "cli-gate3",
            ["referenciaDoInstrumento"] = "pi_demo_1",
            ["fingerprintDoDispositivo"] = "disp-1",
            ["enderecoIp"] = "203.0.113.10",
            ["paisDeOrigem"] = "BR",
            [campo] = campo == "sinais" ? Array.Empty<object>() : 0,
        };

        using var resposta = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracaoA.Chave,
            $"idem-{Guid.NewGuid():N}",
            corpo,
            Cancelamento);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Score_enviado_nao_substitui_o_score_calculado()
    {
        // Mesmo que o contrato fechado ja recuse o campo, a prova completa e
        // esta: a transacao gravada tem o score que o motor produziu, e nao um
        // numero escolhido por quem chamou.
        var corpo = CenarioDeIngestao.Corpo(identificadorExterno: "gate3-score");

        using var resposta = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracaoA.Chave,
            $"idem-{Guid.NewGuid():N}",
            corpo,
            Cancelamento);

        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(resposta);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        var avaliacao = await contexto.AvaliacoesDeRisco
            .SingleAsync(a => a.TransacaoId == transacaoId, Cancelamento);

        Assert.Equal(0, avaliacao.Score);
        Assert.Equal(Decisao.Permitir, avaliacao.Decisao);
    }

    [Fact]
    public async Task Nao_ha_rota_que_altere_avaliacao_regra_ou_perfil()
    {
        // O catalogo de regras e somente leitura ate a Fase 8, que traz
        // rascunho, backtest e publicacao. Uma rota de escrita agora seria um
        // caminho para mudar o comportamento do motor sem nenhuma dessas
        // protecoes (CLAUDE.md secao 23).
        var token = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenantA,
            PerfilDeUsuario.Administrador,
            Cancelamento);

        var tentativas = new (HttpMethod Metodo, string Caminho)[]
        {
            (HttpMethod.Post, "/api/regras"),
            (HttpMethod.Put, $"/api/regras/{Guid.NewGuid()}"),
            (HttpMethod.Delete, $"/api/regras/{Guid.NewGuid()}"),
            (HttpMethod.Post, "/api/regras/perfil"),
            (HttpMethod.Put, "/api/regras/perfil"),
            (HttpMethod.Post, "/api/avaliacoes"),
            (HttpMethod.Put, $"/api/transacoes/{Guid.NewGuid()}/avaliacao"),
        };

        foreach (var (metodo, caminho) in tentativas)
        {
            using var requisicao = CenarioDeIdentidade.Autenticada(metodo, caminho, token);
            requisicao.Content = JsonContent.Create(new { score = 0, decisao = "Permitir" });

            using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

            Assert.True(
                resposta.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{metodo} {caminho} respondeu {(int)resposta.StatusCode} — a rota existe.");
        }
    }

    // -----------------------------------------------------------------------
    // Isolamento entre tenants
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Avaliacao_do_tenant_A_nao_e_alcancavel_pelo_tenant_B()
    {
        // 404, e nao 403: um 403 confirmaria que o identificador existe em
        // algum lugar (CLAUDE.md secao 52).
        using var enviada = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracaoA.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(identificadorExterno: "gate3-isolamento"),
            Cancelamento);

        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(enviada);

        var tokenDeB = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenantB,
            PerfilDeUsuario.Administrador,
            Cancelamento);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Get,
            $"/api/transacoes/{transacaoId}",
            tokenDeB);

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task Listagem_do_tenant_B_nao_mostra_transacao_avaliada_do_tenant_A()
    {
        using var enviada = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracaoA.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(identificadorExterno: "gate3-listagem"),
            Cancelamento);

        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(enviada);

        var tokenDeB = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenantB,
            PerfilDeUsuario.AnalistaDeFraude,
            Cancelamento);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Get,
            "/api/transacoes?tamanho=100",
            tokenDeB);

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);
        resposta.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(
            await resposta.Content.ReadAsStringAsync(Cancelamento));

        Assert.DoesNotContain(
            json.RootElement.GetProperty("itens").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == transacaoId);
    }

    [Fact]
    public async Task Cada_tenant_enxerga_o_proprio_catalogo_de_regras()
    {
        // O catalogo e provisionado por organizacao. Se as regras vazassem
        // entre tenants, a Fase 8 publicaria a regra de um cliente e mudaria a
        // decisao de outro.
        var regrasDeA = await IdsDeRegrasAsync(_tenantA);
        var regrasDeB = await IdsDeRegrasAsync(_tenantB);

        Assert.NotEmpty(regrasDeA);
        Assert.Equal(regrasDeA.Count, regrasDeB.Count);
        Assert.Empty(regrasDeA.Intersect(regrasDeB));
    }

    [Fact]
    public async Task Contexto_historico_nao_atravessa_a_fronteira_do_tenant()
    {
        // O mesmo clienteExternoId em dois tenants: sao clientes diferentes.
        // Se o historico vazasse, a rajada do tenant A faria a primeira
        // transacao do tenant B disparar velocidade.
        var integracaoB = await CenarioDeIngestao.CriarIntegracaoAsync(
            _cliente,
            _tenantB,
            Cancelamento);

        const string ClienteCompartilhado = "cli-mesmo-id";
        var inicio = DateTimeOffset.UtcNow.AddMinutes(-5);

        for (var i = 0; i < 6; i++)
        {
            using var rajada = await CenarioDeIngestao.EnviarAsync(
                _cliente,
                _integracaoA.Chave,
                $"idem-{Guid.NewGuid():N}",
                CenarioDeIngestao.Corpo(
                    identificadorExterno: $"gate3-rajada-{i}",
                    clienteExternoId: ClienteCompartilhado,
                    ocorridaEm: inicio.AddSeconds(i * 10)),
                Cancelamento);

            rajada.EnsureSuccessStatusCode();
        }

        using var primeiraDeB = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            integracaoB.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(
                identificadorExterno: "gate3-primeira-de-b",
                clienteExternoId: ClienteCompartilhado,
                ocorridaEm: inicio.AddSeconds(70)),
            Cancelamento);

        using var json = JsonDocument.Parse(
            await primeiraDeB.Content.ReadAsStringAsync(Cancelamento));

        Assert.Equal(0, json.RootElement.GetProperty("score").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("sinais").EnumerateArray());
    }

    // -----------------------------------------------------------------------
    // Configuracao de regra e dado, nunca codigo
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Configuracao_gravada_e_dado_tipado_sem_expressao_executavel()
    {
        // O que fica no banco sao numeros e o nome de um tipo do catalogo
        // fechado. Nao ha expressao, SQL nem script - e o CLAUDE.md secao 21
        // proibe exatamente isso.
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        var versoes = await contexto.VersoesDeRegra.ToListAsync(Cancelamento);

        Assert.Equal(CatalogoPadraoDeRisco.Definicoes.Count, versoes.Count);

        foreach (var versao in versoes)
        {
            // O tipo lido do banco tem que estar no enum: nenhuma linha pode
            // introduzir um comportamento que o motor nao conheca.
            Assert.True(Enum.IsDefined(versao.Tipo));

            // A configuracao volta como o record tipado correspondente, e nao
            // como texto livre.
            Assert.Equal(versao.Tipo, versao.Configuracao.Tipo);
            Assert.NotEmpty(versao.Configuracao.Descrever());
        }
    }

    [Fact]
    public async Task Regra_adulterada_no_banco_nao_vira_comportamento_novo()
    {
        // Simula o pior caso: alguem com acesso de escrita ao banco troca o
        // tipo gravado por um valor inventado. A leitura falha alto, em vez de
        // executar algo que ninguem revisou.
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        var versao = await contexto.VersoesDeRegra.FirstAsync(Cancelamento);

        const string ConfiguracaoAdulterada =
            "{\"tipo\":\"RegraInventada\",\"maximoDeTransacoes\":1}";

        await contexto.Database.ExecuteSqlAsync(
            $"UPDATE versoes_de_regra SET configuracao = CAST({ConfiguracaoAdulterada} AS jsonb) WHERE id = {versao.Id}",
            Cancelamento);

        await using var leitura = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => leitura.VersoesDeRegra.SingleAsync(v => v.Id == versao.Id, Cancelamento));
    }

    // -----------------------------------------------------------------------
    // Auxiliares
    // -----------------------------------------------------------------------

    private async Task<List<Guid>> IdsDeRegrasAsync(TenantDeTeste tenant)
    {
        var token = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            tenant,
            PerfilDeUsuario.AnalistaDeFraude,
            Cancelamento);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, "/api/regras", token);
        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        resposta.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(
            await resposta.Content.ReadAsStringAsync(Cancelamento));

        return [.. json.RootElement.EnumerateArray().Select(r => r.GetProperty("id").GetGuid())];
    }
}
