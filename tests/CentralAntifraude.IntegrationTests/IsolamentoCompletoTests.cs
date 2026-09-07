using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// A varredura cross-tenant completa (ROADMAP 11.8).
///
/// **Por que uma suite nova, se cada fase ja testou o proprio isolamento.**
/// Porque as fases testaram uma familia de cada vez, e o risco muda de forma
/// quando o produto cresce: uma rota nova de uma fase futura pode nascer
/// isolada e uma rota antiga pode perder o isolamento numa refatoracao. Esta
/// classe monta **um recurso de cada tipo** no tenant B e passa o identificador
/// de cada um pela API do tenant A, em uma tabela so.
///
/// **O identificador e sempre real.** Um GUID sorteado responderia `404` de
/// qualquer jeito e o teste passaria pelo motivo errado — provaria apenas que
/// o recurso nao existe, e nao que o isolamento funciona.
///
/// **A resposta e `404`, e nunca `403`.** Um `403` confirmaria que aquele
/// identificador existe em algum lugar (`CLAUDE.md` secao 52).
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class IsolamentoCompletoTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenantA = null!;
    private TenantDeTeste _tenantB = null!;
    private RecursosDoTenant _doB = null!;
    private CenarioDeMensageria _mensageria = null!;

    private readonly Dictionary<(Guid, PerfilDeUsuario), string> _tokens = [];

    public IsolamentoCompletoTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    /// <summary>Um recurso de cada familia, todos do mesmo tenant.</summary>
    private sealed record RecursosDoTenant(
        Guid UsuarioId,
        Guid IntegracaoId,
        Guid CredencialId,
        Guid TransacaoId,
        Guid AlertaId,
        Guid CasoId,
        Guid RegraId,
        Guid BacktestId);

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        var sufixo = $"{Guid.NewGuid():N}"[..8];

        _tenantA = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"isoa-{sufixo}",
            Cancelamento);

        _tenantB = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"isob-{sufixo}",
            Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();

        await CenarioDeMensageria.LimparAsync(_banco.StringDeConexao, Cancelamento);
        _mensageria = CenarioDeMensageria.Criar(_banco.StringDeConexao);

        _doB = await MontarRecursosAsync(_tenantB);
    }

    public async ValueTask DisposeAsync()
    {
        if (_mensageria is not null)
        {
            await _mensageria.DisposeAsync();
        }

        _cliente?.Dispose();

        if (_fabrica is not null)
        {
            await _fabrica.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Leitura por identificador
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Nenhum_recurso_de_outro_tenant_e_alcancavel_por_identificador()
    {
        // Uma tabela, e nao onze testes: o que se afirma e a mesma coisa para
        // todas as familias, e separa-las esconderia a que faltasse.
        var rotas = new (string Caminho, PerfilDeUsuario Perfil)[]
        {
            ($"/api/usuarios/{_doB.UsuarioId}", PerfilDeUsuario.Administrador),
            ($"/api/integracoes/{_doB.IntegracaoId}", PerfilDeUsuario.Administrador),
            ($"/api/transacoes/{_doB.TransacaoId}", PerfilDeUsuario.AnalistaDeFraude),
            ($"/api/casos/{_doB.CasoId}", PerfilDeUsuario.AnalistaDeFraude),
            ($"/api/regras/{_doB.RegraId}", PerfilDeUsuario.SupervisorDeFraude),
            ($"/api/backtests/{_doB.BacktestId}", PerfilDeUsuario.SupervisorDeFraude),
        };

        foreach (var (caminho, perfil) in rotas)
        {
            using var resposta = await EnviarAsync(HttpMethod.Get, caminho, perfil: perfil);

            Assert.True(
                resposta.StatusCode == HttpStatusCode.NotFound,
                $"{caminho} respondeu {(int)resposta.StatusCode} para o outro tenant.");
        }
    }

    [Fact]
    public async Task Nenhuma_escrita_alcanca_recurso_de_outro_tenant()
    {
        var acoes = new (HttpMethod Metodo, string Caminho, object? Corpo, PerfilDeUsuario Perfil)[]
        {
            (HttpMethod.Post, $"/api/casos/{_doB.CasoId}/notas",
                new { conteudo = "invasao", versao = 1 }, PerfilDeUsuario.AnalistaDeFraude),
            (HttpMethod.Post, $"/api/casos/{_doB.CasoId}/assumir",
                new { versao = 1 }, PerfilDeUsuario.AnalistaDeFraude),
            (HttpMethod.Post, $"/api/casos/{_doB.CasoId}/resolucao",
                new { resultado = "Legitima", versao = 1 }, PerfilDeUsuario.AnalistaDeFraude),
            (HttpMethod.Post, $"/api/casos/{_doB.CasoId}/alertas",
                new { alertaId = Guid.CreateVersion7(), versao = 1 }, PerfilDeUsuario.AnalistaDeFraude),
            (HttpMethod.Post, $"/api/regras/{_doB.RegraId}/publicacao",
                new { versao = 1 }, PerfilDeUsuario.SupervisorDeFraude),
            (HttpMethod.Post, $"/api/regras/{_doB.RegraId}/ativacao",
                new { ativa = false, versao = 1 }, PerfilDeUsuario.SupervisorDeFraude),
            (HttpMethod.Post, $"/api/backtests/{_doB.BacktestId}/cancelamento",
                new { versao = 1 }, PerfilDeUsuario.SupervisorDeFraude),
            (HttpMethod.Put, $"/api/usuarios/{_doB.UsuarioId}/ativacao",
                new { ativo = false }, PerfilDeUsuario.Administrador),
            (HttpMethod.Put, $"/api/integracoes/{_doB.IntegracaoId}/ativacao",
                new { ativa = false }, PerfilDeUsuario.Administrador),
            (HttpMethod.Post, $"/api/integracoes/{_doB.IntegracaoId}/credenciais",
                null, PerfilDeUsuario.Administrador),
            (HttpMethod.Delete,
                $"/api/integracoes/{_doB.IntegracaoId}/credenciais/{_doB.CredencialId}",
                null, PerfilDeUsuario.Administrador),
        };

        foreach (var (metodo, caminho, corpo, perfil) in acoes)
        {
            using var resposta = await EnviarAsync(metodo, caminho, corpo, perfil);

            Assert.True(
                resposta.StatusCode == HttpStatusCode.NotFound,
                $"{metodo} {caminho} respondeu {(int)resposta.StatusCode} para o outro tenant.");
        }
    }

    [Fact]
    public async Task Os_dados_do_outro_tenant_continuam_intactos_depois_das_tentativas()
    {
        // A resposta HTTP nao basta: um `404` devolvido DEPOIS de gravar seria
        // pior do que um `200`. A prova esta no banco.
        await Nenhuma_escrita_alcanca_recurso_de_outro_tenant();

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantB.OrganizacaoId);

        var caso = await contexto.Casos.AsNoTracking()
            .SingleAsync(c => c.Id == _doB.CasoId, Cancelamento);

        Assert.Equal(StatusDoCaso.Novo, caso.Status);
        Assert.Null(caso.ResponsavelId);
        Assert.Equal(0, await contexto.NotasDoCaso.CountAsync(Cancelamento));

        var regra = await contexto.Regras.AsNoTracking()
            .SingleAsync(r => r.Id == _doB.RegraId, Cancelamento);

        Assert.True(regra.Ativa);

        var integracao = await contexto.Integracoes.AsNoTracking()
            .SingleAsync(i => i.Id == _doB.IntegracaoId, Cancelamento);

        Assert.True(integracao.Ativa);

        // Uma credencial a mais significaria que a rotacao passou.
        Assert.Equal(1, await contexto.CredenciaisDeIntegracao.CountAsync(Cancelamento));
    }

    // -----------------------------------------------------------------------
    // Listagens e agregados
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Nenhuma_listagem_mostra_recurso_de_outro_tenant()
    {
        var listas = new (string Caminho, string Colecao, PerfilDeUsuario Perfil)[]
        {
            ("/api/usuarios", "itens", PerfilDeUsuario.Administrador),
            ("/api/integracoes", "itens", PerfilDeUsuario.Administrador),
            ("/api/transacoes", "itens", PerfilDeUsuario.AnalistaDeFraude),
            ("/api/alertas", "itens", PerfilDeUsuario.AnalistaDeFraude),
            ("/api/casos", "itens", PerfilDeUsuario.AnalistaDeFraude),
            ("/api/backtests", "itens", PerfilDeUsuario.SupervisorDeFraude),
            ("/api/auditoria", "itens", PerfilDeUsuario.Auditor),
        };

        var doB = new[]
        {
            _doB.UsuarioId, _doB.IntegracaoId, _doB.TransacaoId,
            _doB.AlertaId, _doB.CasoId, _doB.RegraId, _doB.BacktestId,
        };

        foreach (var (caminho, colecao, perfil) in listas)
        {
            var pagina = await LerAsync(await EnviarAsync(HttpMethod.Get, caminho, perfil: perfil));
            var bruto = pagina.GetProperty(colecao).GetRawText();

            foreach (var id in doB)
            {
                Assert.DoesNotContain(id.ToString(), bruto, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task A_trilha_nao_encontra_entidade_de_outro_tenant_nem_por_identificador()
    {
        // O filtro por entidade aceita um GUID do cliente. Sem o isolamento,
        // seria uma forma de confirmar a existencia de um recurso alheio.
        var trilha = await LerAsync(await EnviarAsync(
            HttpMethod.Get,
            $"/api/auditoria?entidadeId={_doB.IntegracaoId}",
            perfil: PerfilDeUsuario.Auditor));

        Assert.Equal(0, trilha.GetProperty("totalDeItens").GetInt64());
    }

    [Fact]
    public async Task As_regras_vigentes_de_um_tenant_nao_sao_as_do_outro()
    {
        // Perfil de risco e catalogo sao provisionados por organizacao. Duas
        // versoes de perfil com o mesmo numero existem, uma em cada tenant, e
        // nenhuma alcanca a outra.
        var deA = await LerAsync(await EnviarAsync(
            HttpMethod.Get,
            "/api/regras/perfil",
            perfil: PerfilDeUsuario.AnalistaDeFraude));

        var deB = await LerAsync(await EnviarAsync(
            HttpMethod.Get,
            "/api/regras/perfil",
            perfil: PerfilDeUsuario.AnalistaDeFraude,
            organizacao: _tenantB));

        Assert.NotEqual(
            deA.GetProperty("versaoId").GetGuid(),
            deB.GetProperty("versaoId").GetGuid());

        var idsDeA = deA.GetProperty("regras").EnumerateArray()
            .Select(r => r.GetProperty("id").GetGuid()).ToList();

        Assert.DoesNotContain(_doB.RegraId, idsDeA);
    }

    [Fact]
    public async Task O_painel_de_um_tenant_nao_conta_o_movimento_do_outro()
    {
        var deA = await LerAsync(await EnviarAsync(
            HttpMethod.Get,
            "/api/painel?dias=7",
            perfil: PerfilDeUsuario.AnalistaDeFraude));

        var deB = await LerAsync(await EnviarAsync(
            HttpMethod.Get,
            "/api/painel?dias=7",
            perfil: PerfilDeUsuario.AnalistaDeFraude,
            organizacao: _tenantB));

        // B tem a transacao e o alerta montados no preparo; A nao tem nada.
        Assert.Equal(0, deA.GetProperty("transacoesRecebidas").GetInt32());
        Assert.Equal(0, deA.GetProperty("alertasAbertos").GetInt32());

        Assert.True(deB.GetProperty("transacoesRecebidas").GetInt32() > 0);
    }

    // =======================================================================
    // Apoio
    // =======================================================================

    /// <summary>
    /// Monta um recurso de cada familia, pelo caminho real do produto.
    ///
    /// Nada e inserido direto no banco: um caso construido sobre um alerta
    /// forjado nao provaria que o isolamento vale sobre o que o sistema
    /// realmente produz.
    /// </summary>
    private async Task<RecursosDoTenant> MontarRecursosAsync(TenantDeTeste tenant)
    {
        var integracao = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, tenant, Cancelamento);

        var alerta = await CenarioDeInvestigacao.GerarAlertaAsync(
            _cliente,
            _banco.StringDeConexao,
            tenant,
            integracao,
            _mensageria,
            "iso",
            Cancelamento);

        var caso = await LerAsync(await EnviarAsync(
            HttpMethod.Post,
            "/api/casos",
            new { titulo = "Caso do outro tenant", alertasIds = new[] { alerta.Id } },
            PerfilDeUsuario.AnalistaDeFraude,
            tenant));

        var regras = await LerAsync(await EnviarAsync(
            HttpMethod.Get,
            "/api/regras/gestao",
            perfil: PerfilDeUsuario.SupervisorDeFraude,
            organizacao: tenant));

        var regra = regras.EnumerateArray().First();
        var regraId = regra.GetProperty("id").GetGuid();

        var backtest = await LerAsync(await EnviarAsync(
            HttpMethod.Post,
            "/api/backtests",
            new
            {
                regraId = (Guid?)null,
                limiarDeRevisao = 35,
                limiarDeBloqueio = 65,
                inicio = DateTimeOffset.UtcNow.AddDays(-10),
                fim = DateTimeOffset.UtcNow.AddMinutes(5),
            },
            PerfilDeUsuario.SupervisorDeFraude,
            tenant));

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            tenant.OrganizacaoId);

        var credencialId = await contexto.CredenciaisDeIntegracao
            .AsNoTracking()
            .Select(c => c.Id)
            .FirstAsync(Cancelamento);

        return new RecursosDoTenant(
            tenant.UsuariosPorPerfil[PerfilDeUsuario.Auditor],
            integracao.Id,
            credencialId,
            alerta.TransacaoId,
            alerta.Id,
            caso.GetProperty("id").GetGuid(),
            regraId,
            backtest.GetProperty("execucao").GetProperty("id").GetGuid());
    }

    private async Task<HttpResponseMessage> EnviarAsync(
        HttpMethod metodo,
        string caminho,
        object? corpo = null,
        PerfilDeUsuario perfil = PerfilDeUsuario.AnalistaDeFraude,
        TenantDeTeste? organizacao = null)
    {
        var tenant = organizacao ?? _tenantA;
        var token = await TokenAsync(tenant, perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(metodo, caminho, token);

        if (corpo is not null)
        {
            requisicao.Content = JsonContent.Create(corpo);
        }

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

        Assert.True(
            resposta.IsSuccessStatusCode,
            $"resposta {(int)resposta.StatusCode}: {corpo}");

        return JsonDocument.Parse(corpo).RootElement.Clone();
    }
}
