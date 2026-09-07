using System.Net;
using System.Text.Json;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Security Gate 10 — console, painel e auditoria (ROADMAP 10.8).
///
/// A superficie nova desta fase e **consulta**: filtro livre, ordenacao
/// escolhida pelo cliente, paginacao e agregacao. Nenhuma delas escreve nada,
/// e e justamente por isso que sao perigosas — uma consulta que vaza nao deixa
/// rastro no dado, so no que alguem viu.
///
/// Tres riscos proprios:
///
/// 1. **texto do cliente virando consulta** — filtro, busca e campo de
///    ordenacao chegam como texto e precisam parar antes do SQL;
/// 2. **agregacao atravessando tenant** — um painel que somasse organizacoes
///    devolveria numeros plausiveis, e nada denunciaria;
/// 3. **a trilha na mao errada** — a auditoria e um controle sobre quem opera,
///    e quem e auditado nao pode varrer o proprio rastro.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class SecurityGate10Tests : IAsyncLifetime
{
    private const string Transacoes = "/api/transacoes";
    private const string Painel = "/api/painel";
    private const string Auditoria = "/api/auditoria";

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenantA = null!;
    private TenantDeTeste _tenantB = null!;
    private IntegracaoDeTeste _integracaoA = null!;
    private IntegracaoDeTeste _integracaoB = null!;

    private readonly Dictionary<(Guid Organizacao, PerfilDeUsuario Perfil), string> _tokens = [];

    public SecurityGate10Tests(FixtureDoBanco banco) => _banco = banco;

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
            $"sg10a-{sufixo}",
            Cancelamento);

        _tenantB = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"sg10b-{sufixo}",
            Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();

        _integracaoA = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenantA, Cancelamento);
        _integracaoB = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenantB, Cancelamento);
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
    // Texto do cliente virando consulta
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("'; DROP TABLE transacoes; --")]
    [InlineData("\" UNION SELECT * FROM usuarios --")]
    [InlineData("1' AND (SELECT 1 FROM pg_sleep(5))='1")]
    public async Task Injecao_na_busca_e_tratada_como_texto(string payload)
    {
        // O EF parametriza, entao nada disso vira comando. O que este teste
        // afirma e o passo seguinte: a consulta RESPONDE, com zero resultado —
        // e a tabela continua de pe depois.
        await IngerirAsync(_tenantA, _integracaoA, "sqli", "cli-sqli");

        var resposta = await ListarAsync($"busca={Uri.EscapeDataString(payload)}");

        Assert.Equal(0, resposta.GetProperty("total").GetInt32());

        // A tabela continua existindo e com o dado dentro.
        Assert.Equal(1, (await ListarAsync("busca=sqli")).GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("%%")]
    public async Task Curinga_na_busca_nao_amplia_o_resultado(string curinga)
    {
        // Este e o defeito que ninguem reporta: uma lista que volta cheia nao
        // levanta suspeita, e quem consultou acredita ter filtrado.
        await IngerirAsync(_tenantA, _integracaoA, "curinga", "cli-curinga");

        var resposta = await ListarAsync($"busca={Uri.EscapeDataString(curinga + "z")}");

        Assert.Equal(0, resposta.GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("senha")]
    [InlineData("t.Id; DROP TABLE transacoes")]
    [InlineData("organizacaoId")]
    public async Task Ordenacao_por_campo_arbitrario_e_recusada(string campo)
    {
        // O campo de ordenacao nunca vem livre: ele e comparado com uma lista
        // fechada e substituido pelo canonico. Decisao da Fase 0, cobrada
        // aqui.
        using var resposta = await EnviarAsync(
            HttpMethod.Get,
            $"{Transacoes}?ordenarPor={Uri.EscapeDataString(campo)}");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("validacao_falhou", await CodigoAsync(resposta));
    }

    [Theory]
    [InlineData("decisao=2")]
    [InlineData("decisao=99")]
    [InlineData("decisao=Aprovar")]
    [InlineData("tipoDeRegra=1")]
    [InlineData("tipoDeRegra=RegraMagica")]
    public async Task Enum_forjado_no_filtro_e_recusado(string consulta)
    {
        // O numero e o caso traicoeiro: `Enum.TryParse` aceitaria "2" como
        // Revisar e "99" como um valor que nao existe no enum. Comparar com os
        // NOMES fecha os dois buracos.
        using var resposta = await EnviarAsync(HttpMethod.Get, $"{Transacoes}?{consulta}");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Theory]
    [InlineData("operacao=63")]
    [InlineData("operacao=ApagarTudo")]
    public async Task Enum_forjado_no_filtro_de_auditoria_e_recusado(string consulta)
    {
        using var resposta = await EnviarAsync(
            HttpMethod.Get,
            $"{Auditoria}?{consulta}",
            perfil: PerfilDeUsuario.Auditor);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Theory]
    [InlineData("scoreMinimo=-1")]
    [InlineData("scoreMaximo=101")]
    [InlineData("scoreMinimo=80&scoreMaximo=20")]
    public async Task Faixa_de_score_impossivel_e_recusada(string consulta)
    {
        using var resposta = await EnviarAsync(HttpMethod.Get, $"{Transacoes}?{consulta}");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Paginacao abusiva
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData($"{Transacoes}?tamanho=5000")]
    [InlineData($"{Transacoes}?tamanho=0")]
    [InlineData($"{Transacoes}?pagina=0")]
    [InlineData($"{Transacoes}?pagina=-3")]
    public async Task Paginacao_abusiva_e_recusada_no_console(string caminho)
    {
        // Reduzir em silencio para o teto faria o cliente acreditar que
        // recebeu a lista inteira. Numa tela de fraude, acreditar que se viu
        // tudo e pior do que receber um erro.
        using var resposta = await EnviarAsync(HttpMethod.Get, caminho);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Paginacao_abusiva_e_recusada_na_auditoria()
    {
        using var resposta = await EnviarAsync(
            HttpMethod.Get,
            $"{Auditoria}?tamanho=100000",
            perfil: PerfilDeUsuario.Auditor);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3650)]
    public async Task Janela_abusiva_do_painel_e_recusada(int dias)
    {
        // O painel agrega: uma janela de dez anos varre o historico inteiro do
        // tenant para responder uma pergunta que ninguem fez.
        using var resposta = await EnviarAsync(HttpMethod.Get, $"{Painel}?dias={dias}");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Isolamento entre tenants
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_busca_nao_atravessa_organizacao()
    {
        // O identificador do outro tenant e conhecido, e mesmo assim nao
        // aparece: o filtro global responde antes do filtro do cliente.
        await IngerirAsync(_tenantB, _integracaoB, "segredo-do-b", "cli-do-b");

        var doA = await ListarAsync("busca=segredo-do-b");
        var doB = await ListarAsync("busca=segredo-do-b", _tenantB);

        Assert.Equal(0, doA.GetProperty("total").GetInt32());
        Assert.Equal(1, doB.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Transacao_de_outro_tenant_responde_404_no_detalhe()
    {
        var doB = await IngerirAsync(_tenantB, _integracaoB, "detalhe-b", "cli-b");

        using var resposta = await EnviarAsync(HttpMethod.Get, $"{Transacoes}/{doB}");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task O_painel_nao_mistura_organizacoes()
    {
        // O vazamento mais silencioso que este produto poderia ter: numeros
        // plausiveis, sem nada que denunciasse.
        await IngerirAsync(_tenantA, _integracaoA, "pa", "cli-a");
        await IngerirAsync(_tenantA, _integracaoA, "pa2", "cli-a2");
        await IngerirAsync(_tenantB, _integracaoB, "pb", "cli-b");

        var doA = await LerAsync(await EnviarAsync(HttpMethod.Get, $"{Painel}?dias=7"));
        var doB = await LerAsync(await EnviarAsync(
            HttpMethod.Get,
            $"{Painel}?dias=7",
            organizacao: _tenantB));

        Assert.Equal(2, doA.GetProperty("transacoesRecebidas").GetInt32());
        Assert.Equal(1, doB.GetProperty("transacoesRecebidas").GetInt32());

        // Conferido tambem no banco, e nao so pela resposta HTTP.
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantB.OrganizacaoId);

        Assert.Equal(1, await contexto.Transacoes.CountAsync(Cancelamento));
    }

    [Fact]
    public async Task As_metricas_de_regra_nao_misturam_organizacoes()
    {
        await IngerirAsync(_tenantA, _integracaoA, "ma", "cli-ma");

        var doB = await LerAsync(await EnviarAsync(
            HttpMethod.Get,
            $"{Painel}/regras?dias=7",
            organizacao: _tenantB));

        Assert.Empty(doB.EnumerateArray());
    }

    [Fact]
    public async Task A_trilha_de_um_tenant_nao_enxerga_o_outro()
    {
        // As duas organizacoes tiveram integracao criada no preparo, entao as
        // duas trilhas tem registro — e nenhuma pode ver a da outra.
        var doA = await LerAsync(await EnviarAsync(
            HttpMethod.Get,
            Auditoria,
            perfil: PerfilDeUsuario.Auditor));

        var doB = await LerAsync(await EnviarAsync(
            HttpMethod.Get,
            Auditoria,
            perfil: PerfilDeUsuario.Auditor,
            organizacao: _tenantB));

        var idsDeA = doA.GetProperty("itens").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();

        var idsDeB = doB.GetProperty("itens").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();

        Assert.NotEmpty(idsDeA);
        Assert.NotEmpty(idsDeB);
        Assert.Empty(idsDeA.Intersect(idsDeB));
    }

    // -----------------------------------------------------------------------
    // Quem le a trilha
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(PerfilDeUsuario.AnalistaDeFraude)]
    [InlineData(PerfilDeUsuario.SupervisorDeFraude)]
    public async Task Quem_e_auditado_nao_le_a_propria_trilha(PerfilDeUsuario perfil)
    {
        // A trilha e um controle SOBRE o que Supervisor e Analista fazem —
        // publicar regra, resolver caso. Dar a quem e auditado o poder de
        // varrer o proprio rastro enfraquece o unico registro que responde
        // "quem fez o que e quando".
        using var listagem = await EnviarAsync(HttpMethod.Get, Auditoria, perfil: perfil);
        Assert.Equal(HttpStatusCode.Forbidden, listagem.StatusCode);

        using var operacoes = await EnviarAsync(
            HttpMethod.Get,
            $"{Auditoria}/operacoes",
            perfil: perfil);

        Assert.Equal(HttpStatusCode.Forbidden, operacoes.StatusCode);
    }

    [Theory]
    [InlineData(PerfilDeUsuario.Administrador)]
    [InlineData(PerfilDeUsuario.Auditor)]
    public async Task Quem_audita_le_a_trilha(PerfilDeUsuario perfil)
    {
        using var resposta = await EnviarAsync(HttpMethod.Get, Auditoria, perfil: perfil);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task Nao_existe_rota_que_altere_a_trilha(string metodo)
    {
        // Uma trilha alteravel nao prova nada: seria a primeira coisa a ser
        // ajustada quando o resultado incomodasse alguem. A ausencia da rota e
        // a garantia — nao um `if`.
        using var resposta = await EnviarAsync(
            new HttpMethod(metodo),
            Auditoria,
            perfil: PerfilDeUsuario.Administrador);

        Assert.Contains(
            resposta.StatusCode,
            new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }

    [Fact]
    public async Task A_trilha_nao_devolve_segredo_nenhum()
    {
        // A trilha nunca recebeu senha, hash, token nem payload completo — a
        // decisao e da Fase 1. Este teste cobra a promessa na SAIDA, sobre
        // registros reais: o preparo criou integracao e credencial, que sao
        // justamente as operacoes com segredo por perto.
        var corpo = await (await EnviarAsync(
            HttpMethod.Get,
            $"{Auditoria}?tamanho=100",
            perfil: PerfilDeUsuario.Administrador)).Content.ReadAsStringAsync(Cancelamento);

        Assert.NotEmpty(corpo);

        foreach (var proibido in new[]
        {
            "senha",
            "password",
            "hash",
            "token",
            "authorization",
            "bearer",
            "caf_",
            "secret",
        })
        {
            Assert.DoesNotContain(proibido, corpo, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task O_painel_e_o_console_exigem_sessao()
    {
        foreach (var caminho in new[] { Transacoes, Painel, $"{Painel}/regras", Auditoria })
        {
            using var requisicao = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(caminho, UriKind.Relative));

            using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        }
    }

    [Fact]
    public async Task A_credencial_de_integracao_nao_le_o_console()
    {
        // Uma API key nunca vira sessao humana (CLAUDE.md secao 50).
        using var requisicao = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(Transacoes, UriKind.Relative));

        requisicao.Headers.TryAddWithoutValidation("Authorization", $"ApiKey {_integracaoA.Chave}");

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    // =======================================================================
    // Apoio
    // =======================================================================

    private async Task<JsonElement> ListarAsync(string consulta, TenantDeTeste? tenant = null) =>
        await LerAsync(await EnviarAsync(
            HttpMethod.Get,
            $"{Transacoes}?{consulta}",
            organizacao: tenant));

    private async Task<Guid> IngerirAsync(
        TenantDeTeste tenant,
        IntegracaoDeTeste integracao,
        string prefixo,
        string cliente)
    {
        _ = tenant;

        using var resposta = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            integracao.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(
                identificadorExterno: $"{prefixo}-{Guid.NewGuid():N}"[..24],
                clienteExternoId: cliente,
                ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1)),
            Cancelamento);

        resposta.EnsureSuccessStatusCode();

        return await CenarioDeIngestao.IdDaTransacaoAsync(resposta);
    }

    private async Task<HttpResponseMessage> EnviarAsync(
        HttpMethod metodo,
        string caminho,
        PerfilDeUsuario perfil = PerfilDeUsuario.AnalistaDeFraude,
        TenantDeTeste? organizacao = null)
    {
        var tenant = organizacao ?? _tenantA;
        var token = await TokenAsync(tenant, perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(metodo, caminho, token);

        return await _cliente.SendAsync(requisicao, Cancelamento);
    }

    private async Task<string> TokenAsync(TenantDeTeste tenant, PerfilDeUsuario perfil)
    {
        var chave = (tenant.OrganizacaoId, perfil);

        if (_tokens.TryGetValue(chave, out var existente))
        {
            return existente;
        }

        var token = await CenarioDeIngestao.TokenDeAsync(_cliente, tenant, perfil, Cancelamento);
        _tokens[chave] = token;

        return token;
    }

    private static async Task<JsonElement> LerAsync(HttpResponseMessage resposta)
    {
        var corpo = await resposta.Content.ReadAsStringAsync(Cancelamento);

        return JsonDocument.Parse(corpo).RootElement.Clone();
    }

    private static async Task<string?> CodigoAsync(HttpResponseMessage resposta)
    {
        var problema = await LerAsync(resposta);

        return problema.TryGetProperty("codigo", out var codigo) ? codigo.GetString() : null;
    }
}
