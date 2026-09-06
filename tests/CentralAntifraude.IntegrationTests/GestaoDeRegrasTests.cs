using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// A administracao de regras, ponta a ponta e contra o PostgreSQL real.
///
/// **O que esta fase precisa provar sao duas coisas.**
///
/// A primeira e que o passado nao muda: uma transacao avaliada com a regra v1
/// continua explicada pela v1 depois que a v2 e publicada (ROADMAP 8.9). Sem
/// isso, o produto perde a resposta para "por que esta decisao foi tomada
/// naquele momento".
///
/// A segunda e que nada alcanca o motor sem passar por uma versao de perfil
/// publicada. Rascunho nao vale, versao de regra sozinha nao vale, regra
/// desativada sai. E o que torna a tela de regras uma descricao fiel do que o
/// motor esta executando.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class GestaoDeRegrasTests : IAsyncLifetime
{
    private const string Caminho = "/api/regras";

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;

    /// <summary>Um login por perfil: o login tem limite por IP desde a Fase 1.</summary>
    private readonly Dictionary<PerfilDeUsuario, string> _tokens = [];

    public GestaoDeRegrasTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"regra-{Guid.NewGuid():N}"[..14],
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
    // O fluxo inteiro
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Do_rascunho_a_publicacao_a_regra_percorre_o_fluxo_inteiro()
    {
        // 1. Criar. Nasce como rascunho e nao vale para ninguem.
        var criada = await CriarAsync("Velocidade noturna", TipoDeRegra.VelocidadePorCliente, 5, 30, 25);

        var regraId = criada.GetProperty("id").GetGuid();

        Assert.True(criada.GetProperty("ativa").GetBoolean());
        Assert.False(criada.GetProperty("noPerfilVigente").GetBoolean());
        Assert.Equal(JsonValueKind.Null, criada.GetProperty("numeroDaVersaoVigente").ValueKind);
        Assert.Equal(25, criada.GetProperty("rascunho").GetProperty("pontos").GetInt32());

        // O perfil vigente ainda nao tem esta regra: escrever um rascunho nao
        // muda o que o motor executa.
        Assert.Equal(4, (await PerfilVigenteAsync()).GetProperty("regras").GetArrayLength());

        // 2. Editar o rascunho.
        var editada = await SalvarRascunhoAsync(
            regraId,
            "Velocidade noturna",
            new() { ["maximoDeTransacoes"] = 2, ["janelaEmMinutos"] = 30 },
            pontos: 30,
            versao: criada.GetProperty("versao").GetInt32());

        Assert.Equal(30, editada.GetProperty("rascunho").GetProperty("pontos").GetInt32());
        Assert.False(editada.GetProperty("noPerfilVigente").GetBoolean());

        // 3. Publicar. Agora sim o motor passa a executa-la.
        var publicada = await PublicarAsync(regraId, editada.GetProperty("versao").GetInt32());

        Assert.Equal(1, publicada.GetProperty("numeroDaVersaoVigente").GetInt32());
        Assert.Equal(30, publicada.GetProperty("pontosVigentes").GetInt32());
        Assert.True(publicada.GetProperty("noPerfilVigente").GetBoolean());
        Assert.Equal(JsonValueKind.Null, publicada.GetProperty("rascunho").ValueKind);

        var perfil = await PerfilVigenteAsync();

        Assert.Equal(5, perfil.GetProperty("regras").GetArrayLength());
        Assert.Equal(2, perfil.GetProperty("numero").GetInt32());
    }

    [Fact]
    public async Task Duas_regras_do_mesmo_tipo_convivem_com_configuracoes_diferentes()
    {
        // A organizacao ja nasce com uma velocidade curta. Uma segunda, com
        // janela longa, e um caso real de operacao antifraude — e e por isso
        // que o tipo deixou de ser unico por organizacao nesta fase.
        var segunda = await CriarAsync("Velocidade diaria", TipoDeRegra.VelocidadePorCliente, 20, 720, 15);

        await PublicarAsync(segunda.GetProperty("id").GetGuid(), segunda.GetProperty("versao").GetInt32());

        var doTipo = (await PerfilVigenteAsync())
            .GetProperty("regras")
            .EnumerateArray()
            .Count(r => r.GetProperty("tipo").GetString() == nameof(TipoDeRegra.VelocidadePorCliente));

        Assert.Equal(2, doTipo);
    }

    [Fact]
    public async Task Nome_repetido_na_mesma_organizacao_e_recusado()
    {
        // Com duas regras do mesmo tipo permitidas, o nome e o que resta para
        // a lista, a auditoria e a explicacao nao ficarem ambiguas.
        using var resposta = await PostAsync(
            Caminho,
            new
            {
                tipo = nameof(TipoDeRegra.NovoDispositivo),
                nome = "Dispositivo novo",
                configuracao = new Dictionary<string, decimal> { ["minimoDeTransacoesNoHistorico"] = 5 },
                pontos = 10,
            });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("nome_em_uso", await CodigoAsync(resposta));
    }

    // -----------------------------------------------------------------------
    // ROADMAP 8.9 — o passado continua explicavel
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Transacao_avaliada_na_v1_continua_explicada_pela_v1_depois_da_v2()
    {
        // Este e o teste que sustenta a promessa central do produto. Sem ele,
        // uma auditoria de tres meses atras leria a decisao antiga com os
        // numeros de hoje — e a explicacao seria plausivel e falsa.
        var cliente = $"cli-{Guid.NewGuid():N}"[..12];

        // Historico: quatro transacoes pequenas, do mesmo dispositivo e pais.
        for (var i = 0; i < 4; i++)
        {
            await IngerirAsync($"hist-{i}", cliente, valor: 100m);
        }

        // A transacao suspeita: valor muito acima da media do cliente.
        var transacaoId = await IngerirAsync("suspeita", cliente, valor: 5_000m);

        var antes = await TransacaoAsync(transacaoId);
        var sinalAntes = SinalDe(antes, nameof(TipoDeRegra.ValorAcimaDoHistorico));

        Assert.Equal(1, sinalAntes.GetProperty("versaoDaRegra").GetInt32());
        Assert.Equal(30, sinalAntes.GetProperty("pontos").GetInt32());

        var explicacaoOriginal = sinalAntes.GetProperty("explicacao").GetString();
        var perfilOriginal = antes.GetProperty("avaliacao").GetProperty("numeroDaVersaoDePerfil").GetInt32();

        // Agora o Supervisor republica a regra com peso e multiplo diferentes.
        var regra = await RegraPorTipoAsync(TipoDeRegra.ValorAcimaDoHistorico);
        var regraId = regra.GetProperty("id").GetGuid();

        var comRascunho = await SalvarRascunhoAsync(
            regraId,
            regra.GetProperty("nome").GetString()!,
            new() { ["multiploDaMedia"] = 2m, ["minimoDeTransacoesNoHistorico"] = 3 },
            pontos: 90,
            versao: regra.GetProperty("versao").GetInt32());

        await PublicarAsync(regraId, comRascunho.GetProperty("versao").GetInt32());

        // A regra vigente mudou...
        var vigente = RegraNoPerfil(await PerfilVigenteAsync(), regraId);

        Assert.Equal(90, vigente.GetProperty("pontos").GetInt32());
        Assert.Equal(2, vigente.GetProperty("versaoAtual").GetInt32());

        // ...e a avaliacao antiga nao mudou nada.
        var depois = await TransacaoAsync(transacaoId);
        var sinalDepois = SinalDe(depois, nameof(TipoDeRegra.ValorAcimaDoHistorico));

        Assert.Equal(1, sinalDepois.GetProperty("versaoDaRegra").GetInt32());
        Assert.Equal(30, sinalDepois.GetProperty("pontos").GetInt32());
        Assert.Equal(explicacaoOriginal, sinalDepois.GetProperty("explicacao").GetString());

        Assert.Equal(
            antes.GetProperty("avaliacao").GetProperty("score").GetInt32(),
            depois.GetProperty("avaliacao").GetProperty("score").GetInt32());

        Assert.Equal(
            perfilOriginal,
            depois.GetProperty("avaliacao").GetProperty("numeroDaVersaoDePerfil").GetInt32());
    }

    [Fact]
    public async Task A_versao_publicada_continua_no_banco_depois_de_substituida()
    {
        var regra = await RegraPorTipoAsync(TipoDeRegra.NovoDispositivo);
        var regraId = regra.GetProperty("id").GetGuid();

        var comRascunho = await SalvarRascunhoAsync(
            regraId,
            regra.GetProperty("nome").GetString()!,
            new() { ["minimoDeTransacoesNoHistorico"] = 7 },
            pontos: 22,
            versao: regra.GetProperty("versao").GetInt32());

        await PublicarAsync(regraId, comRascunho.GetProperty("versao").GetInt32());

        var detalhe = await ObterAsync(regraId);
        var versoes = detalhe.GetProperty("versoes").EnumerateArray().ToList();

        // As duas versoes existem, e a antiga esta intacta. Substituir e
        // acrescentar, nunca sobrescrever (CLAUDE.md secao 23).
        Assert.Equal(2, versoes.Count);
        Assert.Equal(2, versoes[0].GetProperty("numero").GetInt32());
        Assert.Equal(1, versoes[1].GetProperty("numero").GetInt32());
        Assert.Equal(20, versoes[1].GetProperty("pontos").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Ativacao e limiares
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Desativar_uma_regra_a_tira_do_perfil_sem_apagar_o_historico()
    {
        var regra = await RegraPorTipoAsync(TipoDeRegra.DivergenciaGeografica);
        var regraId = regra.GetProperty("id").GetGuid();

        var desativada = await AtivacaoAsync(regraId, ativa: false, regra.GetProperty("versao").GetInt32());

        Assert.False(desativada.GetProperty("ativa").GetBoolean());
        Assert.False(desativada.GetProperty("noPerfilVigente").GetBoolean());

        // A versao publicada continua existindo: as avaliacoes que a usaram
        // continuam explicaveis.
        Assert.Single(desativada.GetProperty("versoes").EnumerateArray());

        var perfil = await PerfilVigenteAsync();

        Assert.Equal(3, perfil.GetProperty("regras").GetArrayLength());

        // Reativar traz de volta a ultima versao que ela tinha.
        var reativada = await AtivacaoAsync(regraId, ativa: true, desativada.GetProperty("versao").GetInt32());

        Assert.True(reativada.GetProperty("noPerfilVigente").GetBoolean());
        Assert.Equal(4, (await PerfilVigenteAsync()).GetProperty("regras").GetArrayLength());
    }

    [Fact]
    public async Task Desativar_a_ultima_regra_do_perfil_e_recusado()
    {
        // Um perfil sem regra pontuaria tudo com zero e recomendaria Permitir
        // para qualquer transacao — sem erro, sem log, sem ninguem perceber.
        var regras = (await ListarGestaoAsync())
            .EnumerateArray()
            .Select(r => r.GetProperty("id").GetGuid())
            .ToList();

        for (var i = 0; i < regras.Count - 1; i++)
        {
            var detalhe = await ObterAsync(regras[i]);

            await AtivacaoAsync(regras[i], ativa: false, detalhe.GetProperty("versao").GetInt32());
        }

        var ultima = await ObterAsync(regras[^1]);

        using var resposta = await PostAsync(
            $"{Caminho}/{regras[^1]}/ativacao",
            new { ativa = false, versao = ultima.GetProperty("versao").GetInt32() });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("estado_da_regra", await CodigoAsync(resposta));

        // E o perfil continua com a regra que sobrou.
        Assert.Single((await PerfilVigenteAsync()).GetProperty("regras").EnumerateArray());
    }

    [Fact]
    public async Task Publicar_limiares_cria_versao_nova_de_perfil_e_muda_a_proxima_decisao()
    {
        var antes = await PerfilVigenteAsync();

        Assert.Equal(40, antes.GetProperty("limiarDeRevisao").GetInt32());

        using var resposta = await PostAsync(
            $"{Caminho}/perfil/limiares",
            new
            {
                limiarDeRevisao = 15,
                limiarDeBloqueio = 60,
                numeroDaVersaoVigente = antes.GetProperty("numero").GetInt32(),
            });

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);

        var depois = await PerfilVigenteAsync();

        Assert.Equal(15, depois.GetProperty("limiarDeRevisao").GetInt32());
        Assert.Equal(antes.GetProperty("numero").GetInt32() + 1, depois.GetProperty("numero").GetInt32());

        // As regras seguem as mesmas: mexer nos limiares nao republica regra
        // nenhuma, so a composicao do perfil.
        Assert.Equal(
            antes.GetProperty("regras").GetArrayLength(),
            depois.GetProperty("regras").GetArrayLength());
    }

    [Fact]
    public async Task Limiares_invertidos_sao_recusados()
    {
        // Revisao acima do bloqueio deixaria a faixa "revisar" vazia: uma das
        // tres decisoes viraria inalcancavel e ninguem notaria ate perguntar
        // por que nada e revisado.
        var perfil = await PerfilVigenteAsync();

        using var resposta = await PostAsync(
            $"{Caminho}/perfil/limiares",
            new
            {
                limiarDeRevisao = 80,
                limiarDeBloqueio = 20,
                numeroDaVersaoVigente = perfil.GetProperty("numero").GetInt32(),
            });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal(40, (await PerfilVigenteAsync()).GetProperty("limiarDeRevisao").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Rascunho
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Descartar_o_rascunho_devolve_a_regra_a_versao_publicada()
    {
        var regra = await RegraPorTipoAsync(TipoDeRegra.NovoDispositivo);
        var regraId = regra.GetProperty("id").GetGuid();

        var comRascunho = await SalvarRascunhoAsync(
            regraId,
            regra.GetProperty("nome").GetString()!,
            new() { ["minimoDeTransacoesNoHistorico"] = 90 },
            pontos: 99,
            versao: regra.GetProperty("versao").GetInt32());

        var token = await TokenAsync(PerfilDeUsuario.SupervisorDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Delete,
            $"{Caminho}/{regraId}/rascunho?versao={comRascunho.GetProperty("versao").GetInt32()}",
            token);

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        resposta.EnsureSuccessStatusCode();

        var depois = await ObterAsync(regraId);

        Assert.Equal(JsonValueKind.Null, depois.GetProperty("rascunho").ValueKind);
        Assert.Equal(1, depois.GetProperty("numeroDaVersaoVigente").GetInt32());
        Assert.Equal(20, depois.GetProperty("pontosVigentes").GetInt32());
    }

    [Fact]
    public async Task Publicar_um_rascunho_igual_a_versao_em_vigor_e_recusado()
    {
        // Uma v2 identica a v1 faria o historico afirmar uma mudanca que nao
        // houve — e e esse historico que a Fase 9 vai comparar.
        var regra = await RegraPorTipoAsync(TipoDeRegra.DivergenciaGeografica);
        var regraId = regra.GetProperty("id").GetGuid();

        var comRascunho = await SalvarRascunhoAsync(
            regraId,
            regra.GetProperty("nome").GetString()!,
            new() { ["minimoDeTransacoesNoHistorico"] = 3 },
            pontos: 25,
            versao: regra.GetProperty("versao").GetInt32());

        using var resposta = await PostAsync(
            $"{Caminho}/{regraId}/publicacao",
            new { versao = comRascunho.GetProperty("versao").GetInt32() });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("estado_da_regra", await CodigoAsync(resposta));
    }

    [Fact]
    public async Task Publicar_sem_rascunho_e_recusado()
    {
        var regra = await RegraPorTipoAsync(TipoDeRegra.VelocidadePorCliente);

        using var resposta = await PostAsync(
            $"{Caminho}/{regra.GetProperty("id").GetGuid()}/publicacao",
            new { versao = regra.GetProperty("versao").GetInt32() });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Auditoria (ROADMAP 8.7)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_publicacao_deixa_rastro_de_quem_publicou()
    {
        var regra = await RegraPorTipoAsync(TipoDeRegra.ValorAcimaDoHistorico);
        var regraId = regra.GetProperty("id").GetGuid();

        var comRascunho = await SalvarRascunhoAsync(
            regraId,
            regra.GetProperty("nome").GetString()!,
            new() { ["multiploDaMedia"] = 8m, ["minimoDeTransacoesNoHistorico"] = 3 },
            pontos: 40,
            versao: regra.GetProperty("versao").GetInt32());

        await PublicarAsync(regraId, comRascunho.GetProperty("versao").GetInt32());

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var registros = await contexto.RegistrosDeAuditoria
            .Where(r => r.EntidadeId == regraId.ToString())
            .ToListAsync(Cancelamento);

        var supervisor = _tenant.UsuariosPorPerfil[PerfilDeUsuario.SupervisorDeFraude];

        Assert.Contains(registros, r => r.Operacao == OperacaoAuditada.RascunhoDeRegraSalvo);
        Assert.Contains(
            registros,
            r => r.Operacao == OperacaoAuditada.VersaoDeRegraPublicada && r.AutorId == supervisor);

        // A publicacao do perfil tambem entra na trilha, e no proprio perfil.
        var doPerfil = await contexto.RegistrosDeAuditoria
            .Where(r => r.Operacao == OperacaoAuditada.VersaoDePerfilPublicada)
            .ToListAsync(Cancelamento);

        Assert.NotEmpty(doPerfil);
    }

    // -----------------------------------------------------------------------
    // Apoio
    // -----------------------------------------------------------------------

    private async Task<JsonElement> CriarAsync(
        string nome,
        TipoDeRegra tipo,
        decimal primeiro,
        decimal segundo,
        int pontos)
    {
        var configuracao = tipo == TipoDeRegra.VelocidadePorCliente
            ? new Dictionary<string, decimal>
            {
                ["maximoDeTransacoes"] = primeiro,
                ["janelaEmMinutos"] = segundo,
            }
            : new Dictionary<string, decimal> { ["minimoDeTransacoesNoHistorico"] = primeiro };

        using var resposta = await PostAsync(
            Caminho,
            new { tipo = tipo.ToString(), nome, configuracao, pontos });

        resposta.EnsureSuccessStatusCode();

        return await LerAsync(resposta);
    }

    private async Task<JsonElement> SalvarRascunhoAsync(
        Guid regraId,
        string nome,
        Dictionary<string, decimal> configuracao,
        int pontos,
        int versao)
    {
        var token = await TokenAsync(PerfilDeUsuario.SupervisorDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Put,
            $"{Caminho}/{regraId}/rascunho",
            token);

        requisicao.Content = JsonContent.Create(new { nome, configuracao, pontos, versao });

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        resposta.EnsureSuccessStatusCode();

        return await LerAsync(resposta);
    }

    private async Task<JsonElement> PublicarAsync(Guid regraId, int versao)
    {
        using var resposta = await PostAsync($"{Caminho}/{regraId}/publicacao", new { versao });

        resposta.EnsureSuccessStatusCode();

        return await LerAsync(resposta);
    }

    private async Task<JsonElement> AtivacaoAsync(Guid regraId, bool ativa, int versao)
    {
        using var resposta = await PostAsync($"{Caminho}/{regraId}/ativacao", new { ativa, versao });

        resposta.EnsureSuccessStatusCode();

        return await LerAsync(resposta);
    }

    private async Task<HttpResponseMessage> PostAsync(string caminho, object corpo)
    {
        var token = await TokenAsync(PerfilDeUsuario.SupervisorDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Post, caminho, token);
        requisicao.Content = JsonContent.Create(corpo);

        return await _cliente.SendAsync(requisicao, Cancelamento);
    }

    private async Task<JsonElement> GetAsync(
        string caminho,
        PerfilDeUsuario perfil = PerfilDeUsuario.SupervisorDeFraude)
    {
        var token = await TokenAsync(perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, caminho, token);
        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        resposta.EnsureSuccessStatusCode();

        return await LerAsync(resposta);
    }

    private Task<JsonElement> ListarGestaoAsync() => GetAsync($"{Caminho}/gestao");

    private Task<JsonElement> ObterAsync(Guid regraId) => GetAsync($"{Caminho}/{regraId}");

    private Task<JsonElement> PerfilVigenteAsync() => GetAsync($"{Caminho}/perfil");

    private Task<JsonElement> TransacaoAsync(Guid transacaoId) =>
        GetAsync($"/api/transacoes/{transacaoId}");

    private async Task<JsonElement> RegraPorTipoAsync(TipoDeRegra tipo)
    {
        var regras = await ListarGestaoAsync();

        return regras.EnumerateArray().First(r => r.GetProperty("tipo").GetString() == tipo.ToString());
    }

    private async Task<Guid> IngerirAsync(string identificador, string cliente, decimal valor)
    {
        using var resposta = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracao.Chave,
            $"idem-{identificador}-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(
                identificadorExterno: $"{identificador}-{Guid.NewGuid():N}"[..20],
                valor: valor,
                clienteExternoId: cliente,
                ocorridaEm: DateTimeOffset.UtcNow.AddSeconds(-1)),
            Cancelamento);

        resposta.EnsureSuccessStatusCode();

        return await CenarioDeIngestao.IdDaTransacaoAsync(resposta);
    }

    private static JsonElement SinalDe(JsonElement transacao, string tipo) =>
        transacao
            .GetProperty("avaliacao")
            .GetProperty("sinais")
            .EnumerateArray()
            .First(s => s.GetProperty("tipo").GetString() == tipo);

    /// <summary>A regra vigente correspondente a um identificador, na resposta do perfil.</summary>
    private static JsonElement RegraNoPerfil(JsonElement perfil, Guid regraId) =>
        perfil
            .GetProperty("regras")
            .EnumerateArray()
            .First(r => r.GetProperty("id").GetGuid() == regraId);

    private static async Task<JsonElement> LerAsync(HttpResponseMessage resposta)
    {
        var corpo = await resposta.Content.ReadAsStringAsync(Cancelamento);

        return JsonDocument.Parse(corpo).RootElement.Clone();
    }

    private static async Task<string?> CodigoAsync(HttpResponseMessage resposta)
    {
        var corpo = await resposta.Content.ReadAsStringAsync(Cancelamento);

        using var json = JsonDocument.Parse(corpo);

        return json.RootElement.TryGetProperty("codigo", out var codigo) ? codigo.GetString() : null;
    }

    private async Task<string> TokenAsync(PerfilDeUsuario perfil)
    {
        if (_tokens.TryGetValue(perfil, out var guardado))
        {
            return guardado;
        }

        var token = await CenarioDeIngestao.TokenDeAsync(_cliente, _tenant, perfil, Cancelamento);

        _tokens[perfil] = token;

        return token;
    }
}
