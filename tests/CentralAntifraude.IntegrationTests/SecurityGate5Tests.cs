using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Infrastructure.Mensageria;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Security Gate 5 (ROADMAP secao 5.10): o que o consumidor faz com uma
/// mensagem que nao devia estar ali.
///
/// **A premissa desta fase.** Ate agora, tudo que entrava no sistema passava
/// por autenticacao. A fila muda isso: o worker le de um lugar, aplica efeito
/// e nao tem um usuario do outro lado para recusar. O que chega da fila e
/// **dado**, nunca instrucao — e cada teste aqui verifica uma forma diferente
/// de a mensagem tentar ser tratada como instrucao.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class SecurityGate5Tests : IAsyncLifetime
{
    private const string Fila = IFilaDeMensagens.FilaOperacional;

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenantA = null!;
    private TenantDeTeste _tenantB = null!;
    private IntegracaoDeTeste _integracaoA = null!;
    private CenarioDeMensageria _mensageria = null!;

    public SecurityGate5Tests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        var sufixo = $"{Guid.NewGuid():N}"[..8];

        _tenantA = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao, $"g5a-{sufixo}", Cancelamento);

        _tenantB = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao, $"g5b-{sufixo}", Cancelamento);

        await CenarioDeMensageria.LimparAsync(_banco.StringDeConexao, Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();

        _integracaoA = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenantA, Cancelamento);
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
    // Mensagem que nao deveria ser interpretada
    // -----------------------------------------------------------------------

    public static TheoryData<string, string> MensagensRecusadas => new()
    {
        { "nao e json", "isto nao e json" },
        { "json que nao e objeto", "\"apenas um texto\"" },
        { "sem eventId", RemoverCampo(SerializadorDeEnvelope.CampoEventId) },
        { "sem tenantId", RemoverCampo(SerializadorDeEnvelope.CampoTenantId) },
        { "sem payload", RemoverCampo(SerializadorDeEnvelope.CampoPayload) },
        { "sem occurredAt", RemoverCampo(SerializadorDeEnvelope.CampoOccurredAt) },
        { "tipo desconhecido", TrocarCampo(SerializadorDeEnvelope.CampoEventType, "EventoInventado") },
        { "versao desconhecida", TrocarCampo(SerializadorDeEnvelope.CampoVersion, 99) },
        { "versao zero", TrocarCampo(SerializadorDeEnvelope.CampoVersion, 0) },
        { "eventId vazio", TrocarCampo(SerializadorDeEnvelope.CampoEventId, Guid.Empty.ToString()) },
        { "eventId nao e guid", TrocarCampo(SerializadorDeEnvelope.CampoEventId, "nao-e-guid") },
        { "payload nao e objeto", TrocarCampo(SerializadorDeEnvelope.CampoPayload, 42) },
    };

    [Theory]
    [MemberData(nameof(MensagensRecusadas))]
    public async Task Mensagem_que_o_worker_nao_entende_nao_vira_efeito(string caso, string corpo)
    {
        // Doze formas de a mensagem estar errada, e uma unica resposta certa:
        // nenhum efeito, e a mensagem preservada para a DLQ. Nenhuma delas
        // pode virar "efeito com valores padrao" — inventar o que faltava
        // seria pior do que recusar.
        await _mensageria.Fila.EnviarAsync(Fila, corpo, Cancelamento);

        var consumo = await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        Assert.True(consumo.Recusados == 1, $"caso '{caso}' nao foi recusado.");
        Assert.Equal(0, consumo.Processados);

        Assert.Equal(0, await LinhasNaInboxAsync(_tenantA));
        Assert.Equal(0, await TotalNoResumoAsync(_tenantA));
    }

    [Fact]
    public async Task Versao_desconhecida_e_recusada_mesmo_com_o_tipo_certo()
    {
        // Uma v2 chegando em um processo que so entende a v1. O campo `version`
        // existe exatamente para permitir esta recusa SEM tentar interpretar o
        // payload — os campos podem ter mudado de significado, e ler "score"
        // de uma v2 poderia produzir um numero plausivel e errado.
        var corpo = TrocarCampo(SerializadorDeEnvelope.CampoVersion, 2);

        await _mensageria.Fila.EnviarAsync(Fila, corpo, Cancelamento);

        Assert.Equal(1, (await _mensageria.Processador.ConsumirLoteAsync(Cancelamento)).Recusados);

        // E a excecao diz o que era esperado, sem despejar o corpo.
        var erro = Assert.Throws<EnvelopeInvalido>(() => SerializadorDeEnvelope.Desserializar(corpo));

        Assert.Contains("TransacaoAvaliada.v2", erro.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Mensagem forjada e tenant inconsistente
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Envelope_que_declara_um_tenant_e_aponta_para_outro_e_recusado()
    {
        // O ataque mais silencioso possivel nesta camada: a mensagem e valida,
        // o payload e um evento real, mas o `tenantId` foi trocado. Se o worker
        // confiasse no campo, o numero do cliente A apareceria no painel do
        // cliente B — sem erro nenhum no caminho.
        using var resposta = await IngerirAsync("tenant-trocado");
        resposta.EnsureSuccessStatusCode();

        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        var original = Assert.Single(await _mensageria.Fila.ReceberAsync(Fila, 10, Cancelamento));

        await _mensageria.Fila.ApagarAsync(original.Recibo, Cancelamento);

        var forjada = TrocarNoCorpo(
            original.Corpo,
            SerializadorDeEnvelope.CampoTenantId,
            _tenantB.OrganizacaoId.ToString());

        await _mensageria.Fila.EnviarAsync(Fila, forjada, Cancelamento);

        var consumo = await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(1, consumo.Recusados);
        Assert.Equal(0, consumo.Processados);

        // Nada foi somado em lugar nenhum — nem no tenant declarado, nem no
        // verdadeiro.
        Assert.Equal(0, await TotalNoResumoAsync(_tenantB));
        Assert.Equal(0, await TotalNoResumoAsync(_tenantA));
    }

    [Fact]
    public async Task Envelope_que_aponta_para_transacao_inexistente_e_recusado()
    {
        // Mensagem inteiramente fabricada: tenant real, formato correto, e uma
        // transacao que nunca existiu. O worker so aplica efeito sobre um fato
        // que ele consegue confirmar no proprio banco.
        using var resposta = await IngerirAsync("transacao-fantasma");
        resposta.EnsureSuccessStatusCode();

        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        var original = Assert.Single(await _mensageria.Fila.ReceberAsync(Fila, 10, Cancelamento));
        await _mensageria.Fila.ApagarAsync(original.Recibo, Cancelamento);

        var noPayload = TrocarNoPayload(original.Corpo, "transacaoId", Guid.NewGuid().ToString());

        await _mensageria.Fila.EnviarAsync(Fila, noPayload, Cancelamento);

        Assert.Equal(1, (await _mensageria.Processador.ConsumirLoteAsync(Cancelamento)).Recusados);
        Assert.Equal(0, await TotalNoResumoAsync(_tenantA));
    }

    // -----------------------------------------------------------------------
    // Replay
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Replay_de_uma_mensagem_antiga_nao_soma_de_novo()
    {
        // Alguem guarda uma mensagem legitima e a reenvia depois. E
        // indistinguivel de uma reentrega normal — e por isso a resposta e a
        // mesma: a Inbox reconhece o eventId e o efeito nao acontece de novo.
        using var resposta = await IngerirAsync("replay");
        resposta.EnsureSuccessStatusCode();

        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        var mensagem = Assert.Single(await _mensageria.Fila.ReceberAsync(Fila, 10, Cancelamento));
        await _mensageria.Fila.DevolverAsync(mensagem.Recibo, Cancelamento);

        await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(1, await TotalNoResumoAsync(_tenantA));

        // Cinco replays da mesma mensagem.
        for (var i = 0; i < 5; i++)
        {
            await _mensageria.Fila.EnviarAsync(Fila, mensagem.Corpo, Cancelamento);
        }

        var consumo = await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(5, consumo.Repetidos);
        Assert.Equal(0, consumo.Processados);
        Assert.Equal(1, await TotalNoResumoAsync(_tenantA));
        Assert.Equal(1, await LinhasNaInboxAsync(_tenantA));
    }

    [Fact]
    public async Task Eventos_de_tenants_diferentes_somam_cada_um_no_seu_painel()
    {
        // O outro lado do isolamento: eventos legitimos dos dois tenants,
        // processados pelo mesmo worker, no mesmo lote. Cada numero precisa
        // cair no lugar certo.
        var integracaoB = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenantB, Cancelamento);

        using var deA = await IngerirAsync("de-a");
        using var deB = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            integracaoB.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(identificadorExterno: "de-b", clienteExternoId: "cli-b"),
            Cancelamento);

        deA.EnsureSuccessStatusCode();
        deB.EnsureSuccessStatusCode();

        await _mensageria.RodarAteEsvaziarAsync(Cancelamento);

        Assert.Equal(1, await TotalNoResumoAsync(_tenantA));
        Assert.Equal(1, await TotalNoResumoAsync(_tenantB));
    }

    // -----------------------------------------------------------------------
    // Veneno, DLQ e logs
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Mensagem_envenenada_sai_de_circulacao_e_fica_guardada()
    {
        // Uma mensagem que nunca vai dar certo nao pode circular para sempre.
        // Mas tambem nao pode ser apagada: ela e a evidencia do defeito
        // (CLAUDE.md secao 72).
        await _mensageria.Fila.EnviarAsync(Fila, "{\"eventType\":\"faltando o resto\"}", Cancelamento);

        for (var i = 0; i < _mensageria.Opcoes.MaximoDeRecebimentos; i++)
        {
            await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);
        }

        await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(0, await _mensageria.Fila.ContarPendentesAsync(Fila, Cancelamento));
        Assert.Equal(1, await _mensageria.Fila.ContarMortasAsync(Fila, Cancelamento));
    }

    [Fact]
    public async Task Uma_mensagem_envenenada_nao_bloqueia_as_boas()
    {
        // Se a mensagem envenenada segurasse a fila, um unico registro
        // defeituoso pararia a operacao inteira — e o painel simplesmente
        // deixaria de atualizar, sem ninguem entender por que.
        await _mensageria.Fila.EnviarAsync(Fila, "veneno puro", Cancelamento);

        for (var i = 0; i < 4; i++)
        {
            using var resposta = await IngerirAsync($"boa-{i}");
            resposta.EnsureSuccessStatusCode();
        }

        await _mensageria.RodarAteEsvaziarAsync(Cancelamento);

        Assert.Equal(4, await TotalNoResumoAsync(_tenantA));
        Assert.Equal(1, await _mensageria.Fila.ContarMortasAsync(Fila, Cancelamento));
    }

    [Fact]
    public async Task O_log_do_backbone_nao_carrega_conteudo_de_evento_nem_segredo()
    {
        // CLAUDE.md secao 70. O log do caminho assincrono precisa dizer o que
        // aconteceu sem repetir o que estava dentro da mensagem: score,
        // decisao e identificador de cliente sao dado do tenant.
        using var resposta = await IngerirAsync(
            "sem-vazamento",
            cliente: "cli-que-nao-pode-vazar",
            instrumento: "pi_segredo_do_instrumento");

        resposta.EnsureSuccessStatusCode();

        await _mensageria.RodarAteEsvaziarAsync(Cancelamento);

        List<string> registros;
        lock (_fabrica.Registros)
        {
            registros = [.. _fabrica.Registros];
        }

        var texto = string.Join("\n", registros);

        Assert.DoesNotContain("cli-que-nao-pode-vazar", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("pi_segredo_do_instrumento", texto, StringComparison.Ordinal);
        Assert.DoesNotContain(_integracaoA.Chave, texto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fila_de_mortas_nao_e_alcancavel_por_nenhuma_rota_http()
    {
        // A DLQ guarda corpos de mensagens que podem conter dado de qualquer
        // tenant. Nao existe endpoint que a exponha — a inspecao acontece por
        // acesso administrativo ao banco, com o procedimento documentado.
        var token = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenantA,
            Domain.Identidade.PerfilDeUsuario.Administrador,
            Cancelamento);

        var caminhos = new[]
        {
            "/api/mensagens-mortas",
            "/api/dlq",
            "/api/eventos",
            "/api/eventos-de-saida",
            "/api/fila",
        };

        foreach (var caminho in caminhos)
        {
            using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, caminho, token);
            using var leitura = await _cliente.SendAsync(requisicao, Cancelamento);

            Assert.Equal(System.Net.HttpStatusCode.NotFound, leitura.StatusCode);
        }
    }

    // -----------------------------------------------------------------------
    // Auxiliares
    // -----------------------------------------------------------------------

    private Task<HttpResponseMessage> IngerirAsync(
        string identificador,
        string? cliente = null,
        string instrumento = "pi_demo_123") =>
        CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracaoA.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(
                identificadorExterno: identificador,
                clienteExternoId: cliente ?? $"cli-{identificador}",
                referenciaDoInstrumento: instrumento),
            Cancelamento);

    private async Task<int> LinhasNaInboxAsync(TenantDeTeste tenant)
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            tenant.OrganizacaoId);

        // So as marcas da projecao. O criador de alertas e o outro consumidor:
        // contar os dois juntos faria a Inbox parecer ter dobrado sem que
        // nenhum evento tivesse sido processado duas vezes.
        return await contexto.EventosProcessados.CountAsync(
            e => e.Consumidor == ProjecaoDeDecisoesDiarias.NomeDoConsumidor,
            Cancelamento);
    }

    private async Task<int> TotalNoResumoAsync(TenantDeTeste tenant)
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            tenant.OrganizacaoId);

        return await contexto.ResumosDiarios.SumAsync(r => r.Quantidade, Cancelamento);
    }

    /// <summary>Um envelope valido, para os testes deformarem.</summary>
    private static string EnvelopeBase()
    {
        var transacao = Domain.Transacoes.Transacao.Registrar(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "base",
            Domain.Primitivos.Dinheiro.De(100m, "BRL"),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            "cli-base",
            "pi_base",
            null,
            null,
            "BR",
            "idem-base",
            "fingerprint");

        var perfil = CatalogoPadraoDeRisco
            .Provisionar(transacao.OrganizacaoId, DateTimeOffset.UtcNow)
            .VersaoDoPerfil;

        var avaliacao = new MotorDeRisco().Avaliar(
            transacao,
            perfil,
            ContextoDeRisco.Vazio,
            DateTimeOffset.UtcNow);

        var evento = EventoDeSaida.Registrar(
            transacao.OrganizacaoId,
            TransacaoAvaliadaV1.De(transacao, avaliacao),
            avaliacao.AvaliadaEm,
            "correlacao-base");

        return SerializadorDeEnvelope.Serializar(EnvelopeDeEvento.De(evento));
    }

    private static string RemoverCampo(string campo)
    {
        var raiz = System.Text.Json.Nodes.JsonNode.Parse(EnvelopeBase())!.AsObject();
        raiz.Remove(campo);

        return raiz.ToJsonString();
    }

    private static string TrocarCampo(string campo, object valor) =>
        TrocarNoCorpo(EnvelopeBase(), campo, valor);

    private static string TrocarNoCorpo(string corpo, string campo, object valor)
    {
        var raiz = System.Text.Json.Nodes.JsonNode.Parse(corpo)!.AsObject();

        raiz[campo] = valor switch
        {
            int numero => System.Text.Json.Nodes.JsonValue.Create(numero),
            _ => System.Text.Json.Nodes.JsonValue.Create(valor.ToString()),
        };

        return raiz.ToJsonString();
    }

    private static string TrocarNoPayload(string corpo, string campo, string valor)
    {
        var raiz = System.Text.Json.Nodes.JsonNode.Parse(corpo)!.AsObject();

        raiz[SerializadorDeEnvelope.CampoPayload]!.AsObject()[campo] =
            System.Text.Json.Nodes.JsonValue.Create(valor);

        return raiz.ToJsonString();
    }
}
