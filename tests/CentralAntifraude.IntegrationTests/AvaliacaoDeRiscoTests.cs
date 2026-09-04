using System.Net;
using System.Text.Json;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// O motor de risco atravessando o sistema inteiro: HTTP, PostgreSQL real,
/// perfil provisionado com a organizacao e as telas de consulta.
///
/// Os testes unitarios ja provam a semantica de cada regra. O que se prova
/// aqui e diferente e nao poderia ser provado sem banco: que a avaliacao
/// entra na MESMA gravacao da transacao, que ela e persistida com os sinais e
/// as versoes, que o retry devolve a avaliacao original em vez de recalcular,
/// e que nada disso atravessa a fronteira do tenant.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class AvaliacaoDeRiscoTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;

    public AvaliacaoDeRiscoTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"risco-{Guid.NewGuid():N}"[..14],
            Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();

        _integracao = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenant, Cancelamento);
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
    // Ingestao avaliada
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Ingestao_devolve_decisao_de_risco_na_propria_resposta()
    {
        // O caminho critico e sincrono (CLAUDE.md secao 28): o integrador
        // precisa da decisao para seguir com o pagamento, e nao de um
        // "responderei depois".
        var resposta = await EnviarAsync(CenarioDeIngestao.Corpo());

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);

        using var json = await LerAsync(resposta);

        Assert.Equal(0, json.RootElement.GetProperty("score").GetInt32());
        Assert.Equal(nameof(Decisao.Permitir), json.RootElement.GetProperty("decisao").GetString());
        Assert.Empty(json.RootElement.GetProperty("sinais").EnumerateArray());
        Assert.True(json.RootElement.TryGetProperty("avaliadaEm", out _));
    }

    [Fact]
    public async Task Rajada_do_mesmo_cliente_dispara_velocidade_e_leva_a_revisar()
    {
        // Quatro tentativas do mesmo cliente em minutos: o padrao de card
        // testing. As tres primeiras passam; a quarta ultrapassa o limite de 3
        // em 10 minutos e soma os 35 pontos da regra.
        var cliente = $"cli-rajada-{Guid.NewGuid():N}"[..20];
        var inicio = DateTimeOffset.UtcNow.AddMinutes(-5);

        for (var i = 0; i < 3; i++)
        {
            var anterior = await EnviarAsync(CenarioDeIngestao.Corpo(
                identificadorExterno: $"rajada-{i}",
                clienteExternoId: cliente,
                ocorridaEm: inicio.AddSeconds(i * 30)));

            anterior.EnsureSuccessStatusCode();
        }

        var quarta = await EnviarAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: "rajada-3",
            clienteExternoId: cliente,
            ocorridaEm: inicio.AddSeconds(120)));

        using var json = await LerAsync(quarta);

        Assert.Equal(35, json.RootElement.GetProperty("score").GetInt32());
        Assert.Equal(nameof(Decisao.Permitir), json.RootElement.GetProperty("decisao").GetString());

        var sinal = Assert.Single(json.RootElement.GetProperty("sinais").EnumerateArray());

        Assert.Equal(
            nameof(TipoDeRegra.VelocidadePorCliente),
            sinal.GetProperty("tipo").GetString());
        Assert.Equal(
            "4",
            sinal.GetProperty("evidencia").GetProperty("tentativasNaJanela").GetString());
    }

    [Fact]
    public async Task Dispositivo_novo_e_pais_novo_somam_e_a_decisao_vira_revisar()
    {
        // 20 + 25 = 45, acima do limiar de revisao. E o caso que a Fase 13 vai
        // transformar em falso positivo legitimo: viagem gera exatamente este
        // par de sinais.
        var cliente = $"cli-viagem-{Guid.NewGuid():N}"[..20];
        var inicio = DateTimeOffset.UtcNow.AddDays(-10);

        for (var i = 0; i < 3; i++)
        {
            var anterior = await EnviarAsync(CenarioDeIngestao.Corpo(
                identificadorExterno: $"viagem-{i}",
                clienteExternoId: cliente,
                ocorridaEm: inicio.AddDays(i),
                fingerprintDoDispositivo: "disp-de-casa",
                paisDeOrigem: "BR"));

            anterior.EnsureSuccessStatusCode();
        }

        var noExterior = await EnviarAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: "viagem-3",
            clienteExternoId: cliente,
            ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1),
            fingerprintDoDispositivo: "disp-de-viagem",
            paisDeOrigem: "PT"));

        using var json = await LerAsync(noExterior);

        Assert.Equal(45, json.RootElement.GetProperty("score").GetInt32());
        Assert.Equal(nameof(Decisao.Revisar), json.RootElement.GetProperty("decisao").GetString());

        var tipos = json.RootElement.GetProperty("sinais")
            .EnumerateArray()
            .Select(s => s.GetProperty("tipo").GetString())
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [nameof(TipoDeRegra.DivergenciaGeografica), nameof(TipoDeRegra.NovoDispositivo)],
            tipos);
    }

    [Fact]
    public async Task Historico_de_outro_cliente_nao_contamina_a_avaliacao()
    {
        // O contexto e por cliente. Sem esse recorte, um cliente movimentado
        // faria a rajada de outro parecer normal — ou o contrario.
        var barulhento = $"cli-barulho-{Guid.NewGuid():N}"[..20];
        var quieto = $"cli-quieto-{Guid.NewGuid():N}"[..20];
        var inicio = DateTimeOffset.UtcNow.AddMinutes(-5);

        for (var i = 0; i < 6; i++)
        {
            var ruido = await EnviarAsync(CenarioDeIngestao.Corpo(
                identificadorExterno: $"ruido-{i}",
                clienteExternoId: barulhento,
                ocorridaEm: inicio.AddSeconds(i * 10)));

            ruido.EnsureSuccessStatusCode();
        }

        var doQuieto = await EnviarAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: "quieto-1",
            clienteExternoId: quieto,
            ocorridaEm: inicio.AddSeconds(70)));

        using var json = await LerAsync(doQuieto);

        Assert.Equal(0, json.RootElement.GetProperty("score").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Persistencia e replay
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Avaliacao_e_gravada_junto_da_transacao_com_sinais_e_versoes()
    {
        var cliente = $"cli-grav-{Guid.NewGuid():N}"[..20];
        var inicio = DateTimeOffset.UtcNow.AddDays(-10);

        for (var i = 0; i < 3; i++)
        {
            var anterior = await EnviarAsync(CenarioDeIngestao.Corpo(
                identificadorExterno: $"grav-{i}",
                clienteExternoId: cliente,
                ocorridaEm: inicio.AddDays(i)));

            anterior.EnsureSuccessStatusCode();
        }

        var resposta = await EnviarAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: "grav-3",
            clienteExternoId: cliente,
            ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1),
            paisDeOrigem: "RU"));

        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(resposta);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var avaliacao = await contexto.AvaliacoesDeRisco
            .SingleAsync(a => a.TransacaoId == transacaoId, Cancelamento);

        Assert.Equal(_tenant.OrganizacaoId, avaliacao.OrganizacaoId);
        Assert.Equal(25, avaliacao.Score);
        Assert.Equal(Decisao.Permitir, avaliacao.Decisao);
        Assert.Equal(AvaliacaoDeRisco.VersaoDoMotor, avaliacao.VersaoDoMotorUsada);

        var sinal = Assert.Single(avaliacao.Sinais);

        Assert.Equal(TipoDeRegra.DivergenciaGeografica, sinal.Tipo);
        Assert.Equal(1, sinal.NumeroDaVersaoDeRegra);
        Assert.NotEqual(Guid.Empty, sinal.VersaoDeRegraId);
        Assert.Contains("RU", sinal.Explicacao, StringComparison.Ordinal);

        // A versao de perfil apontada precisa existir de verdade: e ela que
        // sustenta a explicacao anos depois.
        var versaoDoPerfil = await contexto.VersoesDePerfilDeRisco
            .SingleAsync(v => v.Id == avaliacao.VersaoDePerfilId, Cancelamento);

        Assert.Equal(CatalogoPadraoDeRisco.LimiarDeRevisao, versaoDoPerfil.LimiarDeRevisao);
        Assert.Equal(CatalogoPadraoDeRisco.LimiarDeBloqueio, versaoDoPerfil.LimiarDeBloqueio);
    }

    [Fact]
    public async Task Retry_devolve_a_avaliacao_original_e_nao_cria_uma_segunda()
    {
        // Reavaliar no retry faria o mesmo pedido receber decisoes diferentes
        // conforme o historico crescesse entre a primeira tentativa e a
        // repeticao — e o integrador nao teria como saber qual das duas vale.
        var corpo = CenarioDeIngestao.Corpo(
            identificadorExterno: "retry-avaliado",
            clienteExternoId: $"cli-retry-{Guid.NewGuid():N}"[..20]);

        var chave = $"idem-{Guid.NewGuid():N}";

        var primeira = await EnviarAsync(corpo, chave);
        var segunda = await EnviarAsync(corpo, chave);

        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);
        Assert.Equal("ja_registrada", await CenarioDeIngestao.SituacaoAsync(segunda));

        using var jsonPrimeira = await LerAsync(primeira);
        using var jsonSegunda = await LerAsync(segunda);

        // Mesmo instante de avaliacao: a segunda resposta veio do banco, e nao
        // de um calculo novo.
        Assert.Equal(
            jsonPrimeira.RootElement.GetProperty("avaliadaEm").GetDateTimeOffset(),
            jsonSegunda.RootElement.GetProperty("avaliadaEm").GetDateTimeOffset());

        Assert.Equal(
            jsonPrimeira.RootElement.GetProperty("score").GetInt32(),
            jsonSegunda.RootElement.GetProperty("score").GetInt32());

        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(primeira);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        Assert.Equal(
            1,
            await contexto.AvaliacoesDeRisco.CountAsync(a => a.TransacaoId == transacaoId, Cancelamento));
    }

    [Fact]
    public async Task Vinte_requisicoes_simultaneas_produzem_uma_unica_avaliacao()
    {
        // A restricao unica de avaliacao por transacao existe no banco, e nao
        // so no codigo: e ela que sobrevive a concorrencia. Duas avaliacoes da
        // mesma transacao seriam duas decisoes concorrentes, e nenhuma tela
        // saberia qual mostrar.
        var corpo = CenarioDeIngestao.Corpo(
            identificadorExterno: "concorrencia-avaliada",
            clienteExternoId: $"cli-conc-{Guid.NewGuid():N}"[..20]);

        var chave = $"idem-{Guid.NewGuid():N}";

        var respostas = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => EnviarAsync(corpo, chave)));

        var criadas = respostas.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(1, criadas);

        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(
            respostas.First(r => r.StatusCode == HttpStatusCode.Created));

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        Assert.Equal(
            1,
            await contexto.AvaliacoesDeRisco.CountAsync(a => a.TransacaoId == transacaoId, Cancelamento));

        foreach (var resposta in respostas)
        {
            resposta.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Telas de consulta
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Listagem_de_transacoes_traz_score_e_decisao()
    {
        var enviada = await EnviarAsync(CenarioDeIngestao.Corpo(identificadorExterno: "listagem-1"));
        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(enviada);

        using var json = await ObterComoAnalistaAsync("/api/transacoes?tamanho=50");

        var item = json.RootElement.GetProperty("itens")
            .EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == transacaoId);

        Assert.Equal(0, item.GetProperty("score").GetInt32());
        Assert.Equal(nameof(Decisao.Permitir), item.GetProperty("decisao").GetString());
    }

    [Fact]
    public async Task Detalhe_da_transacao_traz_os_sinais_com_contribuicao_e_versao()
    {
        var cliente = $"cli-detalhe-{Guid.NewGuid():N}"[..20];
        var inicio = DateTimeOffset.UtcNow.AddDays(-10);

        for (var i = 0; i < 3; i++)
        {
            var anterior = await EnviarAsync(CenarioDeIngestao.Corpo(
                identificadorExterno: $"detalhe-{i}",
                clienteExternoId: cliente,
                ocorridaEm: inicio.AddDays(i)));

            anterior.EnsureSuccessStatusCode();
        }

        var avaliada = await EnviarAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: "detalhe-3",
            clienteExternoId: cliente,
            ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1),
            fingerprintDoDispositivo: "disp-desconhecido"));

        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(avaliada);

        using var json = await ObterComoAnalistaAsync($"/api/transacoes/{transacaoId}");

        var avaliacao = json.RootElement.GetProperty("avaliacao");

        Assert.Equal(20, avaliacao.GetProperty("score").GetInt32());
        Assert.Equal(20, avaliacao.GetProperty("somaBrutaDosPontos").GetInt32());
        Assert.False(avaliacao.GetProperty("scoreFoiLimitado").GetBoolean());
        Assert.Equal(1, avaliacao.GetProperty("numeroDaVersaoDePerfil").GetInt32());
        Assert.Equal(AvaliacaoDeRisco.VersaoDoMotor, avaliacao.GetProperty("versaoDoMotor").GetString());

        var sinal = Assert.Single(avaliacao.GetProperty("sinais").EnumerateArray());

        Assert.Equal(nameof(TipoDeRegra.NovoDispositivo), sinal.GetProperty("tipo").GetString());
        Assert.Equal(20, sinal.GetProperty("pontos").GetInt32());
        Assert.Equal(1, sinal.GetProperty("versaoDaRegra").GetInt32());
        Assert.NotEmpty(sinal.GetProperty("explicacao").GetString()!);
    }

    [Fact]
    public async Task Detalhe_da_transacao_nao_expoe_o_fingerprint_de_ip()
    {
        // O endereco bruto nunca chega ao banco (CLAUDE.md secao 57), e o HMAC
        // derivado dele nao ajuda a investigacao — exibi-lo so ampliaria a
        // superficie de dado sensivel na tela.
        var enviada = await EnviarAsync(CenarioDeIngestao.Corpo(identificadorExterno: "sem-ip"));
        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(enviada);

        var corpo = await TextoComoAnalistaAsync($"/api/transacoes/{transacaoId}");

        Assert.DoesNotContain("fingerprintDoIp", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enderecoIp", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("203.0.113.10", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catalogo_de_regras_e_publicado_junto_da_organizacao()
    {
        using var json = await ObterComoAnalistaAsync("/api/regras");

        var regras = json.RootElement.EnumerateArray().ToList();

        Assert.Equal(CatalogoPadraoDeRisco.Definicoes.Count, regras.Count);

        foreach (var definicao in CatalogoPadraoDeRisco.Definicoes)
        {
            var regra = regras.Single(r => r.GetProperty("tipo").GetString() == definicao.Tipo.ToString());

            Assert.Equal(definicao.Pontos, regra.GetProperty("pontos").GetInt32());
            Assert.Equal(1, regra.GetProperty("versaoAtual").GetInt32());

            // A configuracao vai como texto legivel, e nao como o JSON cru:
            // a tela precisa dizer o que a regra faz, nao despejar estrutura.
            Assert.NotEmpty(regra.GetProperty("configuracao").GetString()!);
        }
    }

    [Fact]
    public async Task Perfil_vigente_expoe_os_limiares_em_uso()
    {
        using var json = await ObterComoAnalistaAsync("/api/regras/perfil");

        Assert.Equal(
            CatalogoPadraoDeRisco.LimiarDeRevisao,
            json.RootElement.GetProperty("limiarDeRevisao").GetInt32());
        Assert.Equal(
            CatalogoPadraoDeRisco.LimiarDeBloqueio,
            json.RootElement.GetProperty("limiarDeBloqueio").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("numero").GetInt32());
        Assert.Equal(
            CatalogoPadraoDeRisco.Definicoes.Count,
            json.RootElement.GetProperty("regras").GetArrayLength());
    }

    // -----------------------------------------------------------------------
    // Auxiliares
    // -----------------------------------------------------------------------

    private Task<HttpResponseMessage> EnviarAsync(object corpo, string? chaveDeIdempotencia = null) =>
        CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracao.Chave,
            chaveDeIdempotencia ?? $"idem-{Guid.NewGuid():N}",
            corpo,
            Cancelamento);

    private static async Task<JsonDocument> LerAsync(HttpResponseMessage resposta)
    {
        var texto = await resposta.Content.ReadAsStringAsync(Cancelamento);

        if (!resposta.IsSuccessStatusCode)
        {
            Assert.Fail(
                $"Esperava sucesso. Status {(int)resposta.StatusCode}. Corpo: {texto}");
        }

        return JsonDocument.Parse(texto);
    }

    private async Task<string> TextoComoAnalistaAsync(string caminho)
    {
        var token = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenant,
            PerfilDeUsuario.AnalistaDeFraude,
            Cancelamento);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, caminho, token);
        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        var texto = await resposta.Content.ReadAsStringAsync(Cancelamento);

        if (!resposta.IsSuccessStatusCode)
        {
            Assert.Fail(
                $"Esperava sucesso em {caminho}. Status {(int)resposta.StatusCode}. Corpo: {texto}");
        }

        return texto;
    }

    private async Task<JsonDocument> ObterComoAnalistaAsync(string caminho) =>
        JsonDocument.Parse(await TextoComoAnalistaAsync(caminho));
}
