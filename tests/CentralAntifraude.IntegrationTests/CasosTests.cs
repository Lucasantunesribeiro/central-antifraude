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
/// A investigacao humana, ponta a ponta.
///
/// **O que esta fase precisa provar e que a historia bate com o estado.** Cada
/// acao muda o caso e deixa uma entrada na timeline, na mesma operacao; e o
/// resultado, quando vem, vira dado operacional por transacao — que e o que a
/// Fase 9 vai comparar contra regras candidatas.
///
/// O cenario central e o **falso positivo legitimo**: o motor recomendou
/// revisar, a pessoa concluiu que era legitima. O `CLAUDE.md` secao 94 exige
/// que o produto saiba demonstrar exatamente isso.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class CasosTests : IAsyncLifetime
{
    private const string Caminho = "/api/casos";

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;
    private CenarioDeMensageria _mensageria = null!;

    /// <summary>
    /// Um login por perfil, e nao um por chamada.
    ///
    /// O login tem limite de requisicoes por IP desde a Fase 1, e um teste que
    /// faz vinte acoes faria vinte logins do mesmo endereco — batendo no limite
    /// e falhando por um motivo que nao tem nada a ver com o que ele afirma.
    /// </summary>
    private readonly Dictionary<PerfilDeUsuario, string> _tokens = [];

    public CasosTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"caso-{Guid.NewGuid():N}"[..14],
            Cancelamento);

        await CenarioDeMensageria.LimparAsync(_banco.StringDeConexao, Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();

        _integracao = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenant, Cancelamento);
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
    // O fluxo inteiro
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Do_alerta_a_conclusao_o_caso_percorre_o_fluxo_inteiro()
    {
        var alerta = await AlertaAsync("fluxo");

        // 1. Abrir
        using var abertura = await AbrirAsync("Viagem ao exterior", [alerta.Id]);

        Assert.Equal(HttpStatusCode.Created, abertura.StatusCode);

        var caso = await LerAsync(abertura);

        Assert.Equal(nameof(StatusDoCaso.Novo), Texto(caso, "status"));
        Assert.Null(Texto(caso, "resultado"));
        Assert.Single(caso.GetProperty("alertas").EnumerateArray());

        var casoId = caso.GetProperty("id").GetGuid();

        // 2. Assumir — o caso entra em analise por consequencia
        var depoisDeAssumir = await AcaoAsync(casoId, "assumir", new { versao = Versao(caso) });

        Assert.Equal(nameof(StatusDoCaso.EmAnalise), Texto(depoisDeAssumir, "status"));
        Assert.NotNull(Texto(depoisDeAssumir, "responsavelNome"));

        // 3. Anotar
        var depoisDaNota = await NotaEEstadoAsync(
            casoId,
            "Cliente confirmou a compra por telefone.",
            Versao(depoisDeAssumir));

        Assert.Single(depoisDaNota.GetProperty("notas").EnumerateArray());

        // 4. Resolver como legitima — o falso positivo da narrativa
        var resolvido = await AcaoAsync(
            casoId,
            "resolucao",
            new { resultado = nameof(ResultadoDaInvestigacao.Legitima), versao = Versao(depoisDaNota) });

        Assert.Equal(nameof(StatusDoCaso.Resolvido), Texto(resolvido, "status"));
        Assert.Equal(nameof(ResultadoDaInvestigacao.Legitima), Texto(resolvido, "resultado"));
        Assert.NotNull(Texto(resolvido, "resolvidoPorNome"));

        // A decisao automatica continua sendo o que era: o resultado humano
        // NAO reescreve a avaliacao (CLAUDE.md secao 11).
        var alertaResolvido = Assert.Single(resolvido.GetProperty("alertas").EnumerateArray());

        Assert.Equal("Revisar", alertaResolvido.GetProperty("decisao").GetString());
        Assert.Equal(nameof(StatusDoAlerta.Encerrado), alertaResolvido.GetProperty("status").GetString());

        // 5. E o veredito virou dado operacional por transacao
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var veredito = Assert.Single(await contexto.ResultadosDeInvestigacao.ToListAsync(Cancelamento));

        Assert.Equal(alerta.TransacaoId, veredito.TransacaoId);
        Assert.Equal(casoId, veredito.CasoId);
        Assert.Equal(ResultadoDaInvestigacao.Legitima, veredito.Resultado);
    }

    [Fact]
    public async Task A_timeline_conta_a_historia_na_ordem_em_que_aconteceu()
    {
        var alerta = await AlertaAsync("timeline");

        using var abertura = await AbrirAsync("Historia completa", [alerta.Id]);
        var caso = await LerAsync(abertura);
        var casoId = caso.GetProperty("id").GetGuid();

        var assumido = await AcaoAsync(casoId, "assumir", new { versao = Versao(caso) });
        var comNota = await NotaEEstadoAsync(casoId, "Primeira anotacao da apuracao.", Versao(assumido));

        var resolvido = await AcaoAsync(
            casoId,
            "resolucao",
            new { resultado = nameof(ResultadoDaInvestigacao.FraudeConfirmada), versao = Versao(comNota) });

        var tipos = resolvido.GetProperty("timeline")
            .EnumerateArray()
            .Select(e => e.GetProperty("tipo").GetString())
            .ToList();

        Assert.Equal(
            [
                nameof(TipoDeEventoDoCaso.CasoAberto),
                nameof(TipoDeEventoDoCaso.AlertaAssociado),
                nameof(TipoDeEventoDoCaso.Atribuido),
                nameof(TipoDeEventoDoCaso.NotaAdicionada),
                nameof(TipoDeEventoDoCaso.Resolvido),
            ],
            tipos);

        // Cada entrada diz quem agiu. O nome vai por copia: se a pessoa for
        // renomeada depois, a timeline continua dizendo quem era.
        Assert.All(
            resolvido.GetProperty("timeline").EnumerateArray(),
            e => Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("autorNome").GetString())));
    }

    [Fact]
    public async Task Um_caso_pode_reunir_varios_alertas()
    {
        // CLAUDE.md secao 65: um caso reune um ou mais alertas. E o que separa
        // caso de alerta — se fosse um para um, seriam a mesma coisa.
        var primeiro = await AlertaAsync("multi-1");
        var segundo = await AlertaAsync("multi-2", bloquear: true);

        using var abertura = await AbrirAsync("Dois alertas do mesmo padrao", [primeiro.Id, segundo.Id]);
        var caso = await LerAsync(abertura);

        Assert.Equal(2, caso.GetProperty("alertas").EnumerateArray().Count());

        var casoId = caso.GetProperty("id").GetGuid();
        var assumido = await AcaoAsync(casoId, "assumir", new { versao = Versao(caso) });

        var resolvido = await AcaoAsync(
            casoId,
            "resolucao",
            new { resultado = nameof(ResultadoDaInvestigacao.FraudeConfirmada), versao = Versao(assumido) });

        Assert.Equal(nameof(StatusDoCaso.Resolvido), Texto(resolvido, "status"));

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        // Um veredito por transacao, e nao um por caso.
        Assert.Equal(2, await contexto.ResultadosDeInvestigacao.CountAsync(Cancelamento));
    }

    [Fact]
    public async Task Um_alerta_pode_ser_trazido_para_um_caso_ja_aberto()
    {
        var primeiro = await AlertaAsync("assoc-1");
        var segundo = await AlertaAsync("assoc-2");

        using var abertura = await AbrirAsync("Comeca com um", [primeiro.Id]);
        var caso = await LerAsync(abertura);
        var casoId = caso.GetProperty("id").GetGuid();

        var depois = await AcaoAsync(
            casoId,
            "alertas",
            new { alertaId = segundo.Id, versao = Versao(caso) });

        Assert.Equal(2, depois.GetProperty("alertas").EnumerateArray().Count());

        Assert.Contains(
            depois.GetProperty("timeline").EnumerateArray(),
            e => e.GetProperty("tipo").GetString() == nameof(TipoDeEventoDoCaso.AlertaAssociado) &&
                 e.GetProperty("referenciaId").GetGuid() == segundo.Id);
    }

    [Fact]
    public async Task Supervisor_transfere_o_caso_e_a_timeline_registra_quem_transferiu()
    {
        var alerta = await AlertaAsync("transf");

        using var abertura = await AbrirAsync("Para transferir", [alerta.Id]);
        var caso = await LerAsync(abertura);
        var casoId = caso.GetProperty("id").GetGuid();

        var analista = await UsuarioIdAsync(PerfilDeUsuario.AnalistaDeFraude);

        var depois = await AcaoAsync(
            casoId,
            "transferir",
            new { paraUsuarioId = analista, versao = Versao(caso) },
            PerfilDeUsuario.SupervisorDeFraude);

        Assert.Equal(analista, depois.GetProperty("responsavelId").GetGuid());
        Assert.Equal(nameof(StatusDoCaso.EmAnalise), Texto(depois, "status"));

        var evento = depois.GetProperty("timeline").EnumerateArray().Last();

        Assert.Equal(nameof(TipoDeEventoDoCaso.Transferido), evento.GetProperty("tipo").GetString());
    }

    // -----------------------------------------------------------------------
    // As acoes que o servidor oferece
    // -----------------------------------------------------------------------

    [Fact]
    public async Task As_acoes_permitidas_vem_do_servidor_e_mudam_com_o_estado()
    {
        // CLAUDE.md secao 52: esconder botao nao e autorizacao. A tela mostra o
        // que o servidor disser, e o servidor recusa de novo quando a acao
        // chega — as duas coisas.
        var alerta = await AlertaAsync("acoes");

        using var abertura = await AbrirAsync("Acoes", [alerta.Id]);
        var caso = await LerAsync(abertura);
        var casoId = caso.GetProperty("id").GetGuid();

        // Caso novo, visto por um analista: da para assumir, anotar e associar.
        Assert.Equal(
            ["adicionarNota", "associarAlerta", "assumir"],
            Acoes(caso));

        var assumido = await AcaoAsync(casoId, "assumir", new { versao = Versao(caso) });

        // Assumido pelo proprio: agora da para resolver, e nao da mais para
        // assumir.
        Assert.Equal(
            ["adicionarNota", "associarAlerta", "resolver"],
            Acoes(assumido));

        var resolvido = await AcaoAsync(
            casoId,
            "resolucao",
            new { resultado = nameof(ResultadoDaInvestigacao.Inconclusiva), versao = Versao(assumido) });

        // Resolvido: nada.
        Assert.Empty(Acoes(resolvido));
    }

    [Fact]
    public async Task O_supervisor_ve_transferir_e_o_analista_nao()
    {
        var alerta = await AlertaAsync("acoes-perfil");

        using var abertura = await AbrirAsync("Perfis", [alerta.Id]);
        var caso = await LerAsync(abertura);
        var casoId = caso.GetProperty("id").GetGuid();

        Assert.DoesNotContain("transferir", Acoes(caso));

        var comoSupervisor = await ObterAsync(casoId, PerfilDeUsuario.SupervisorDeFraude);

        Assert.Contains("transferir", Acoes(comoSupervisor));
    }

    // -----------------------------------------------------------------------
    // O efeito na fila de alertas
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Um_alerta_levado_para_um_caso_sai_da_fila_de_trabalho()
    {
        // **A fila e trabalho, e nao historico.** Sem o filtro de situacao, um
        // alerta ja investigado ficaria la para sempre e a tela deixaria de
        // dizer o que ainda precisa de gente.
        var alerta = await AlertaAsync("sai-da-fila");

        Assert.Equal(1, await AlertasNaFilaAsync("status=Aberto"));

        using var abertura = await AbrirAsync("Tirando da fila", [alerta.Id]);
        var caso = await LerAsync(abertura);
        var casoId = caso.GetProperty("id").GetGuid();

        Assert.Equal(0, await AlertasNaFilaAsync("status=Aberto"));
        Assert.Equal(1, await AlertasNaFilaAsync("status=EmCaso"));

        // E o alerta sabe voltar para o caso: o caminho existe nos dois
        // sentidos.
        using var lista = await ListarAlertasAsync("status=EmCaso");
        using var json = JsonDocument.Parse(await lista.Content.ReadAsStringAsync(Cancelamento));

        var naFila = Assert.Single(json.RootElement.GetProperty("itens").EnumerateArray());

        Assert.Equal(casoId, naFila.GetProperty("casoId").GetGuid());

        // Resolvido, ele sai tambem de "em caso".
        var assumido = await AcaoAsync(casoId, "assumir", new { versao = Versao(caso) });

        await AcaoAsync(
            casoId,
            "resolucao",
            new { resultado = nameof(ResultadoDaInvestigacao.Legitima), versao = Versao(assumido) });

        Assert.Equal(0, await AlertasNaFilaAsync("status=EmCaso"));
        Assert.Equal(1, await AlertasNaFilaAsync("status=Encerrado"));
    }

    // -----------------------------------------------------------------------
    // Listagem
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_lista_filtra_por_status_resultado_e_responsavel()
    {
        var aberto = await AlertaAsync("lista-1");
        var resolvido = await AlertaAsync("lista-2");

        using var primeira = await AbrirAsync("Caso em aberto", [aberto.Id]);
        await LerAsync(primeira);

        using var segunda = await AbrirAsync("Caso a resolver", [resolvido.Id]);
        var paraResolver = await LerAsync(segunda);
        var id = paraResolver.GetProperty("id").GetGuid();

        var assumido = await AcaoAsync(id, "assumir", new { versao = Versao(paraResolver) });

        await AcaoAsync(
            id,
            "resolucao",
            new { resultado = nameof(ResultadoDaInvestigacao.Legitima), versao = Versao(assumido) });

        Assert.Equal(2, await TotalAsync(string.Empty));
        Assert.Equal(1, await TotalAsync("status=Novo"));
        Assert.Equal(1, await TotalAsync("status=Resolvido"));
        Assert.Equal(0, await TotalAsync("status=EmAnalise"));
        Assert.Equal(1, await TotalAsync("resultado=Legitima"));
        Assert.Equal(0, await TotalAsync("resultado=FraudeConfirmada"));

        // "Sem responsavel" e o que a operacao precisa para distribuir.
        Assert.Equal(1, await TotalAsync("semResponsavel=true"));

        var analista = await UsuarioIdAsync(PerfilDeUsuario.AnalistaDeFraude);
        Assert.Equal(1, await TotalAsync($"responsavelId={analista}"));
    }

    [Fact]
    public async Task A_lista_resume_os_alertas_sem_uma_consulta_por_linha()
    {
        var medio = await AlertaAsync("resumo-1");
        var alto = await AlertaAsync("resumo-2", bloquear: true);

        using var abertura = await AbrirAsync("Dois pesos", [medio.Id, alto.Id]);
        await LerAsync(abertura);

        using var lista = await ListarAsync(string.Empty, PerfilDeUsuario.AnalistaDeFraude);
        using var json = JsonDocument.Parse(await lista.Content.ReadAsStringAsync(Cancelamento));

        var caso = Assert.Single(json.RootElement.GetProperty("itens").EnumerateArray());

        Assert.Equal(2, caso.GetProperty("quantidadeDeAlertas").GetInt32());
        Assert.Equal(75, caso.GetProperty("maiorScore").GetInt32());

        // **Nao "Media".** A prioridade e gravada como texto, e o maximo de
        // texto e alfabetico: "Media" venceria "Alta". A consulta usa uma
        // expressao de gravidade, e nao a coluna.
        Assert.Equal("Alta", caso.GetProperty("maiorPrioridade").GetString());
    }

    // -----------------------------------------------------------------------
    // Apoio
    // -----------------------------------------------------------------------

    private Task<Alerta> AlertaAsync(string prefixo, bool bloquear = false) =>
        CenarioDeInvestigacao.GerarAlertaAsync(
            _cliente,
            _banco.StringDeConexao,
            _tenant,
            _integracao,
            _mensageria,
            prefixo,
            Cancelamento,
            bloquear);

    private async Task<HttpResponseMessage> AbrirAsync(string titulo, IReadOnlyList<Guid> alertasIds)
    {
        var token = await TokenAsync(PerfilDeUsuario.AnalistaDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Post, Caminho, token);
        requisicao.Content = JsonContent.Create(new { titulo, alertasIds });

        return await _cliente.SendAsync(requisicao, Cancelamento);
    }

    private async Task<JsonElement> AcaoAsync(
        Guid casoId,
        string acao,
        object corpo,
        PerfilDeUsuario perfil = PerfilDeUsuario.AnalistaDeFraude)
    {
        var token = await TokenAsync(perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Post,
            $"{Caminho}/{casoId}/{acao}",
            token);

        requisicao.Content = JsonContent.Create(corpo);

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        resposta.EnsureSuccessStatusCode();

        return await LerAsync(resposta);
    }

    /// <summary>Adiciona uma nota e devolve o estado atualizado do caso.</summary>
    private async Task<JsonElement> NotaEEstadoAsync(Guid casoId, string conteudo, int versao)
    {
        var token = await TokenAsync(PerfilDeUsuario.AnalistaDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Post,
            $"{Caminho}/{casoId}/notas",
            token);

        requisicao.Content = JsonContent.Create(new { conteudo, versao });

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        resposta.EnsureSuccessStatusCode();

        return await ObterAsync(casoId, PerfilDeUsuario.AnalistaDeFraude);
    }

    private async Task<JsonElement> ObterAsync(Guid casoId, PerfilDeUsuario perfil)
    {
        var token = await TokenAsync(perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Get,
            $"{Caminho}/{casoId}",
            token);

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        resposta.EnsureSuccessStatusCode();

        return await LerAsync(resposta);
    }

    private async Task<HttpResponseMessage> ListarAsync(string consulta, PerfilDeUsuario perfil)
    {
        var token = await TokenAsync(perfil);
        var caminho = string.IsNullOrEmpty(consulta) ? Caminho : $"{Caminho}?{consulta}";

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, caminho, token);

        return await _cliente.SendAsync(requisicao, Cancelamento);
    }

    private async Task<long> TotalAsync(string consulta)
    {
        using var resposta = await ListarAsync(consulta, PerfilDeUsuario.AnalistaDeFraude);

        resposta.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(Cancelamento));

        return json.RootElement.GetProperty("total").GetInt64();
    }

    private async Task<HttpResponseMessage> ListarAlertasAsync(string consulta)
    {
        var token = await TokenAsync(PerfilDeUsuario.AnalistaDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Get,
            $"/api/alertas?{consulta}",
            token);

        return await _cliente.SendAsync(requisicao, Cancelamento);
    }

    private async Task<long> AlertasNaFilaAsync(string consulta)
    {
        using var resposta = await ListarAlertasAsync(consulta);

        resposta.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(Cancelamento));

        return json.RootElement.GetProperty("total").GetInt64();
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

    private async Task<Guid> UsuarioIdAsync(PerfilDeUsuario perfil)
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        // Por PERFIL, e nao por e-mail: `Email` e um value object convertido
        // para coluna, e comparar a propriedade interna nao e traduzivel para
        // SQL. Cada tenant de teste tem um usuario de cada perfil.
        return await contexto.Usuarios
            .Where(u => u.Perfil == perfil)
            .Select(u => u.Id)
            .SingleAsync(Cancelamento);
    }

    private static async Task<JsonElement> LerAsync(HttpResponseMessage resposta)
    {
        var texto = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return JsonDocument.Parse(texto).RootElement.Clone();
    }

    private static int Versao(JsonElement caso) => caso.GetProperty("versao").GetInt32();

    private static string? Texto(JsonElement caso, string campo) =>
        caso.GetProperty(campo).ValueKind == JsonValueKind.Null
            ? null
            : caso.GetProperty(campo).GetString();

    private static IReadOnlyList<string> Acoes(JsonElement caso) =>
        [.. caso.GetProperty("acoesPermitidas").EnumerateArray().Select(a => a.GetString()!)];
}
