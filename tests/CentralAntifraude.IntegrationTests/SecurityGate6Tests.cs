using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Security Gate 6 — a fila operacional.
///
/// A superficie nova desta fase e pequena e o gate reflete isso: **uma rota de
/// leitura**. O que ele precisa provar, entao, e sobretudo o que NAO existe —
/// nao ha rota que crie, altere, atribua ou apague alerta, e a ausencia
/// precisa ser verificada, e nao prometida.
///
/// O resto e o de sempre com uma tela nova: um tenant nao ve a fila do outro,
/// filtro forjado e recusado em vez de ignorado, e o alerta nao carrega para a
/// tela nada que o produto escolheu nao expor.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class SecurityGate6Tests : IAsyncLifetime
{
    private const string Caminho = "/api/alertas";

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenantA = null!;
    private TenantDeTeste _tenantB = null!;
    private IntegracaoDeTeste _integracaoA = null!;
    private IntegracaoDeTeste _integracaoB = null!;
    private CenarioDeMensageria _mensageria = null!;

    public SecurityGate6Tests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenantA = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"sg6a-{Guid.NewGuid():N}"[..12],
            Cancelamento);

        _tenantB = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"sg6b-{Guid.NewGuid():N}"[..12],
            Cancelamento);

        await CenarioDeMensageria.LimparAsync(_banco.StringDeConexao, Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();

        _integracaoA = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenantA, Cancelamento);
        _integracaoB = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenantB, Cancelamento);
        _mensageria = CenarioDeMensageria.Criar(_banco.StringDeConexao);
    }

    public async ValueTask DisposeAsync()
    {
        _cliente?.Dispose();

        if (_mensageria is not null)
        {
            await _mensageria.DisposeAsync();
        }

        if (_fabrica is not null)
        {
            await _fabrica.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // 1. Isolamento de tenant
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Um_tenant_nunca_ve_o_alerta_do_outro()
    {
        await GerarAlertaAsync(_integracaoA, "iso-a");
        await GerarAlertaAsync(_integracaoB, "iso-b");

        // Dois alertas existem no banco. Cada tenant enxerga exatamente um —
        // e nao o mesmo.
        var deA = await AlertasDaFilaAsync(_tenantA, PerfilDeUsuario.AnalistaDeFraude);
        var deB = await AlertasDaFilaAsync(_tenantB, PerfilDeUsuario.AnalistaDeFraude);

        var idDeA = Assert.Single(deA);
        var idDeB = Assert.Single(deB);

        Assert.NotEqual(idDeA, idDeB);

        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        // Os dois alertas pertencem a organizacoes diferentes: o teste
        // confirma no banco, e nao so pela resposta HTTP.
        var organizacoes = await contexto.Alertas
            .IgnoreQueryFilters()
            .Where(a => a.Id == idDeA || a.Id == idDeB)
            .Select(a => a.OrganizacaoId)
            .ToListAsync(Cancelamento);

        Assert.Equal(2, organizacoes.Distinct().Count());
    }

    [Fact]
    public async Task Nenhum_parametro_troca_a_organizacao_da_consulta()
    {
        await GerarAlertaAsync(_integracaoA, "param-a");

        // O tenant vem da identidade autenticada, e nao do que o cliente
        // manda. Nem query string, nem cabecalho, nem corpo mudam isso.
        var tentativas = new[]
        {
            $"?organizacaoId={_tenantA.OrganizacaoId}",
            $"?tenantId={_tenantA.OrganizacaoId}",
            $"?organizacao={_tenantA.OrganizacaoId}",
        };

        foreach (var tentativa in tentativas)
        {
            var alertas = await AlertasDaFilaAsync(
                _tenantB,
                PerfilDeUsuario.AnalistaDeFraude,
                tentativa);

            Assert.Empty(alertas);
        }
    }

    [Fact]
    public async Task Sem_autenticacao_a_fila_nao_responde()
    {
        using var resposta = await _cliente.GetAsync(new Uri(Caminho, UriKind.Relative), Cancelamento);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task A_chave_de_integracao_nao_abre_a_fila_humana()
    {
        // Credencial de maquina serve para ingerir, e nao para ler a fila de
        // trabalho de ninguem (CLAUDE.md secao 50).
        using var requisicao = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(Caminho, UriKind.Relative));

        requisicao.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{CenarioDeIngestao.EsquemaDaChave} {_integracaoA.Chave}");

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // 2. Perfis
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(PerfilDeUsuario.Administrador)]
    [InlineData(PerfilDeUsuario.SupervisorDeFraude)]
    [InlineData(PerfilDeUsuario.AnalistaDeFraude)]
    [InlineData(PerfilDeUsuario.Auditor)]
    public async Task Todo_perfil_autenticado_le_a_fila_da_propria_organizacao(PerfilDeUsuario perfil)
    {
        // Inclusive o Auditor: consultar decisoes e trilha e o trabalho dele
        // (CLAUDE.md secao 8.3). O que o Auditor nao pode e AGIR — e nesta
        // fase nao ha o que agir, para ninguem.
        await GerarAlertaAsync(_integracaoA, $"perfil-{perfil}");

        Assert.Single(await AlertasDaFilaAsync(_tenantA, perfil));
    }

    // -----------------------------------------------------------------------
    // 3. A ausencia de escrita
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Nenhum_perfil_altera_alerta_porque_nao_existe_rota_de_escrita()
    {
        // **Este e o teste central do gate.** Alerta e resultado de avaliacao,
        // produzido pelo consumidor a partir de um evento que o proprio
        // sistema publicou. Abrir escrita criaria um caminho para inventar
        // trabalho de investigacao a partir de um JSON — sem avaliacao, sem
        // sinais e sem explicacao.
        //
        // A acao humana sobre um alerta pertence ao CASO, que e a Fase 7.
        await GerarAlertaAsync(_integracaoA, "sem-escrita");

        var alertaId = Assert.Single(await AlertasDaFilaAsync(_tenantA, PerfilDeUsuario.AnalistaDeFraude));

        var token = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenantA,
            PerfilDeUsuario.Administrador,
            Cancelamento);

        var caminhos = new[]
        {
            (HttpMethod.Post, Caminho),
            (HttpMethod.Put, $"{Caminho}/{alertaId}"),
            (HttpMethod.Patch, $"{Caminho}/{alertaId}"),
            (HttpMethod.Delete, $"{Caminho}/{alertaId}"),
            (HttpMethod.Post, $"{Caminho}/{alertaId}/resolver"),
            (HttpMethod.Post, $"{Caminho}/{alertaId}/atribuir"),
            (HttpMethod.Post, $"{Caminho}/em-lote"),
        };

        foreach (var (metodo, caminho) in caminhos)
        {
            using var requisicao = CenarioDeIdentidade.Autenticada(metodo, caminho, token);
            requisicao.Content = JsonContent.Create(new { status = "Resolvido", prioridade = "Alta" });

            using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

            Assert.True(
                resposta.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{metodo} {caminho} devolveu {(int)resposta.StatusCode}, e nao 404/405.");
        }

        // E o alerta continua exatamente como nasceu.
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        var alerta = await contexto.Alertas.SingleAsync(a => a.Id == alertaId, Cancelamento);

        Assert.Equal(StatusDoAlerta.Aberto, alerta.Status);
        Assert.Equal(PrioridadeDeAlerta.Media, alerta.Prioridade);
    }

    [Fact]
    public async Task O_analista_continua_sem_alterar_regra()
    {
        // ROADMAP 6.7: "Analista nao altera regra". Continua valendo — o que
        // mudou na Fase 8 e o motivo da recusa.
        //
        // Ate a Fase 7 a resposta era 404, porque a rota nao existia. Agora
        // ela existe e responde **403**: gerir regras e supervisao
        // (`CLAUDE.md` secao 8.2). Para o analista, o efeito e o mesmo; para
        // este teste, a diferenca importa, porque um 404 aqui passaria a
        // significar "digitei o caminho errado" em vez de "voce nao pode".
        var token = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            Cancelamento);

        using var criacao = CenarioDeIdentidade.Autenticada(HttpMethod.Post, "/api/regras", token);
        criacao.Content = JsonContent.Create(new { pontos = 100 });

        using var respostaDaCriacao = await _cliente.SendAsync(criacao, Cancelamento);

        Assert.Equal(HttpStatusCode.Forbidden, respostaDaCriacao.StatusCode);

        // E os metodos que nao existem continuam nao existindo: nao ha
        // `PUT /api/regras` nem `DELETE /api/regras` para ninguem.
        foreach (var metodo in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            using var requisicao = CenarioDeIdentidade.Autenticada(metodo, "/api/regras", token);
            requisicao.Content = JsonContent.Create(new { pontos = 100 });

            using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

            Assert.True(
                resposta.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{metodo} /api/regras devolveu {(int)resposta.StatusCode}.");
        }
    }

    // -----------------------------------------------------------------------
    // 4. Filtros manipulados
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("decisao=Inventada")]
    [InlineData("decisao=2")]
    [InlineData("prioridade=Critica")]
    [InlineData("prioridade=99")]
    [InlineData("scoreMinimo=-1")]
    [InlineData("scoreMinimo=101")]
    [InlineData("ordenarPor=organizacao_id")]
    [InlineData("ordenarPor=score;DROP TABLE alertas")]
    [InlineData("direcao=aleatoria")]
    [InlineData("tamanho=5000")]
    [InlineData("pagina=0")]
    public async Task Filtro_forjado_e_recusado_e_nao_ignorado(string consulta)
    {
        // Recusar, e nunca ignorar. Um filtro descartado em silencio
        // devolveria a fila inteira, e quem consultou acreditaria estar vendo
        // so o que pediu.
        await GerarAlertaAsync(_integracaoA, "forjado");

        var token = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            Cancelamento);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Get,
            $"{Caminho}?{consulta}",
            token);

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);

        // Contrato de erro consistente, sem detalhe de infraestrutura.
        var corpo = await resposta.Content.ReadAsStringAsync(Cancelamento);

        Assert.DoesNotContain("Npgsql", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", corpo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Campo_desconhecido_na_consulta_e_simplesmente_ignorado_sem_afetar_o_resultado()
    {
        // Query string nao e corpo JSON: um parametro que a rota nao declara
        // nao vira filtro nem erro. O que importa e que ele NAO mude o
        // resultado — e o teste de tenant acima ja prova que ele nao troca a
        // organizacao.
        //
        // Os nomes aqui sao inventados de proposito. `status` era um deles ate
        // a Fase 7, quando o alerta ganhou situacao e o filtro passou a
        // existir de verdade — e ai um valor invalido vira 400, que e o
        // comportamento certo.
        await GerarAlertaAsync(_integracaoA, "extra");

        Assert.Single(await AlertasDaFilaAsync(
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            "?campoQueNaoExiste=1&inventado=Resolvido"));
    }

    // -----------------------------------------------------------------------
    // 5. O que o alerta NAO leva para a tela
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_alerta_nao_expoe_instrumento_dispositivo_nem_ip()
    {
        // A fila mostra o que ajuda a priorizar. Referencia de instrumento,
        // fingerprint de dispositivo e o HMAC de IP nao ajudam a priorizar e
        // ampliariam a superficie de dado sensivel numa tela que fica aberta o
        // dia inteiro (CLAUDE.md secoes 56 e 57).
        await GerarAlertaAsync(_integracaoA, "minimo");

        var token = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            Cancelamento);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, Caminho, token);
        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        resposta.EnsureSuccessStatusCode();

        var corpo = await resposta.Content.ReadAsStringAsync(Cancelamento);

        // O que se procura sao os VALORES, e nao palavras. O tipo de regra
        // `NovoDispositivo` aparece legitimamente entre os sinais: ele diz que
        // o aparelho era novo, sem dizer qual aparelho e.
        Assert.DoesNotContain("pi_demo", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disp-novo", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disp-de-casa", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("203.0.113", corpo, StringComparison.Ordinal);

        // E nem os campos que os carregariam.
        Assert.DoesNotContain("referenciaDoInstrumento", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enderecoIp", corpo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evento_de_outro_tenant_nunca_vira_alerta_neste()
    {
        // O worker confere o tenant declarado contra o dado antes de qualquer
        // efeito. Um envelope forjado que apontasse para a transacao de outro
        // tenant colocaria o alerta na fila do cliente errado — e o cliente
        // errado veria dado que nao e dele.
        //
        // A mecanica ja e verificada em detalhe no Security Gate 5; aqui o que
        // importa e a consequencia nova: nenhum alerta atravessa.
        await GerarAlertaAsync(_integracaoA, "forja");

        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        var alertas = await contexto.Alertas
            .IgnoreQueryFilters()
            .Where(a => a.OrganizacaoId == _tenantB.OrganizacaoId)
            .CountAsync(Cancelamento);

        Assert.Equal(0, alertas);
    }

    // -----------------------------------------------------------------------
    // Apoio
    // -----------------------------------------------------------------------

    /// <summary>
    /// Ingere um cenario que produz `Revisar` (dispositivo novo + pais novo =
    /// 45) e roda o backbone ate o alerta existir.
    /// </summary>
    private async Task GerarAlertaAsync(IntegracaoDeTeste integracao, string prefixo)
    {
        var cliente = $"cli-{prefixo}-{Guid.NewGuid():N}"[..24];
        var inicio = DateTimeOffset.UtcNow.AddDays(-10);

        for (var i = 0; i < 3; i++)
        {
            using var historico = await CenarioDeIngestao.EnviarAsync(
                _cliente,
                integracao.Chave,
                $"idem-{Guid.NewGuid():N}",
                CenarioDeIngestao.Corpo(
                    identificadorExterno: $"{prefixo}-hist-{i}",
                    clienteExternoId: cliente,
                    ocorridaEm: inicio.AddDays(i),
                    fingerprintDoDispositivo: "disp-de-casa",
                    paisDeOrigem: "BR"),
                Cancelamento);

            historico.EnsureSuccessStatusCode();
        }

        using var alvo = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            integracao.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(
                identificadorExterno: $"{prefixo}-alvo",
                clienteExternoId: cliente,
                ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1),
                fingerprintDoDispositivo: "disp-novo",
                paisDeOrigem: "PT"),
            Cancelamento);

        alvo.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await alvo.Content.ReadAsStringAsync(Cancelamento));

        Assert.Equal(
            nameof(Decisao.Revisar),
            json.RootElement.GetProperty("decisao").GetString());

        await _mensageria.RodarAteEsvaziarAsync(Cancelamento);
    }

    private async Task<IReadOnlyList<Guid>> AlertasDaFilaAsync(
        TenantDeTeste tenant,
        PerfilDeUsuario perfil,
        string consulta = "")
    {
        var token = await CenarioDeIngestao.TokenDeAsync(_cliente, tenant, perfil, Cancelamento);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Get,
            $"{Caminho}{consulta}",
            token);

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        resposta.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(Cancelamento));

        return [.. json.RootElement.GetProperty("itens")
            .EnumerateArray()
            .Select(a => a.GetProperty("id").GetGuid())];
    }
}
