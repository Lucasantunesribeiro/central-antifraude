using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Domain.Backtests;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Backtests, ponta a ponta e contra o PostgreSQL real.
///
/// **Duas coisas precisam ser provadas aqui, e o resto e consequencia.**
///
/// A primeira e que o backtest roda o MESMO motor da producao (ROADMAP 9.4).
/// Nao basta afirmar isso num comentario: o teste simula um candidato igual ao
/// perfil vigente e exige que cada decisao bata com a avaliacao que ficou
/// gravada na ingestao. Se o recorte do contexto historico divergisse — uma
/// janela diferente, um teto diferente, uma ordem diferente — os numeros nao
/// fechariam.
///
/// A segunda e que producao nao e tocada (ROADMAP 9.5). As contagens de
/// avaliacoes, sinais, alertas, casos, versoes de regra, versoes de perfil e
/// eventos sao conferidas antes e depois: um backtest so pode escrever na
/// propria linha.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class BacktestsTests : IAsyncLifetime
{
    private const string Caminho = "/api/backtests";
    private const string CaminhoDeRegras = "/api/regras";

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;
    private CenarioDeMensageria _mensageria = null!;

    /// <summary>Um login por perfil: o login tem limite por IP desde a Fase 1.</summary>
    private readonly Dictionary<PerfilDeUsuario, string> _tokens = [];

    public BacktestsTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"bkt-{Guid.NewGuid():N}"[..14],
            Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();
        _integracao = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenant, Cancelamento);

        // O despachante le a Outbox de todos os tenants. Sem limpar o acumulo
        // das outras classes, "quantas mensagens tem na fila" deixaria de ter
        // resposta estavel — mesma razao da Fase 5.
        await CenarioDeMensageria.LimparAsync(_banco.StringDeConexao, Cancelamento);

        _mensageria = CenarioDeMensageria.Criar(_banco.StringDeConexao);
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
    // O fluxo inteiro
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Do_pedido_a_conclusao_o_backtest_percorre_o_fluxo_inteiro()
    {
        await IngerirLoteAsync("fluxo", quantidade: 4);

        var regra = await AbrirRascunhoDeVelocidadeAsync(maximo: 1, janela: 60, pontos: 60);

        // 1. Pedir. O POST nao executa nada: ele congela e enfileira.
        var pedido = await SolicitarAsync(regraId: regra);

        Assert.Equal(nameof(StatusDoBacktest.Pendente), Status(pedido));
        Assert.Equal(JsonValueKind.Null, pedido.GetProperty("resultado").ValueKind);

        var execucaoId = pedido.GetProperty("execucao").GetProperty("id").GetGuid();

        // O candidato ja esta congelado na resposta, com a origem de cada
        // regra visivel: e assim que a tela mostra "esta e a mudanca".
        var candidata = pedido
            .GetProperty("candidato")
            .GetProperty("regras")
            .EnumerateArray()
            .Single(r => r.GetProperty("regraId").GetGuid() == regra);

        Assert.Equal(
            nameof(OrigemDaRegraCandidata.Rascunho),
            candidata.GetProperty("origem").GetString());

        // 2. Rodar. Despacho da Outbox + consumo da fila de backtests.
        await _mensageria.RodarBacktestsAteEsvaziarAsync(Cancelamento);

        // 3. Ler o resultado.
        var concluido = await ObterAsync(execucaoId);

        Assert.Equal(nameof(StatusDoBacktest.Concluida), Status(concluido));

        var resultado = concluido.GetProperty("resultado");

        Assert.Equal(4, resultado.GetProperty("totalAnalisado").GetInt32());
        Assert.True(resultado.GetProperty("totalDeMudancas").GetInt32() > 0);

        // A regra candidata e severa (1 transacao em 60 minutos, 60 pontos):
        // o candidato precisa ser mais rigido do que o vigente.
        var mudancas = resultado.GetProperty("mudancas").EnumerateArray().ToList();

        Assert.All(mudancas, m => Assert.Equal("Permitir", m.GetProperty("de").GetString()));
    }

    // -----------------------------------------------------------------------
    // Mesmo motor (ROADMAP 9.4)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Candidato_igual_ao_vigente_reproduz_as_decisoes_ja_gravadas()
    {
        // **Este e o teste que sustenta a fase.** Se o backtest tivesse motor
        // proprio, ou se o contexto historico fosse recortado de outro jeito,
        // os numeros nao bateriam com as avaliacoes reais.
        var transacoes = await IngerirLoteAsync("espelho", quantidade: 6);

        var decisoesReais = await DecisoesGravadasAsync(transacoes);

        // Um candidato sem regra alterada: so os limiares, iguais aos que ja
        // valem. Tudo o mais e identico ao perfil vigente.
        var perfil = await PerfilVigenteAsync();

        var pedido = await SolicitarAsync(
            regraId: null,
            limiarDeRevisao: perfil.GetProperty("limiarDeRevisao").GetInt32(),
            limiarDeBloqueio: perfil.GetProperty("limiarDeBloqueio").GetInt32());

        await _mensageria.RodarBacktestsAteEsvaziarAsync(Cancelamento);

        var resultado = (await ObterAsync(Id(pedido))).GetProperty("resultado");

        Assert.Equal(transacoes.Count, resultado.GetProperty("totalAnalisado").GetInt32());

        // Nenhuma decisao muda: o candidato e o vigente.
        Assert.Empty(resultado.GetProperty("mudancas").EnumerateArray());
        Assert.Equal(0, resultado.GetProperty("totalDeMudancas").GetInt32());

        // E a distribuicao recalculada bate, decisao a decisao, com o que o
        // motor decidiu de verdade na ingestao.
        var vigente = resultado.GetProperty("vigente");

        foreach (var (decisao, quantidade) in decisoesReais)
        {
            Assert.Equal(
                quantidade,
                vigente.GetProperty(decisao.ToString().ToLowerInvariant()).GetInt32());
        }
    }

    // -----------------------------------------------------------------------
    // Isolamento de producao (ROADMAP 9.5)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_backtest_nao_escreve_nada_em_producao()
    {
        await IngerirLoteAsync("isolado", quantidade: 5);
        await _mensageria.RodarAteEsvaziarAsync(Cancelamento);

        var regra = await AbrirRascunhoDeVelocidadeAsync(maximo: 1, janela: 60, pontos: 90);

        var antes = await ContagensDeProducaoAsync();
        var eventosAntes = await EventosPorTipoAsync();

        var pedido = await SolicitarAsync(regraId: regra);
        await _mensageria.RodarBacktestsAteEsvaziarAsync(Cancelamento);

        Assert.Equal(nameof(StatusDoBacktest.Concluida), Status(await ObterAsync(Id(pedido))));

        // Avaliacao, sinal, alerta, caso, versao de regra, versao de perfil,
        // transacao e veredito: nada muda. O backtest so pode escrever na
        // propria linha.
        Assert.Equal(antes, await ContagensDeProducaoAsync());

        // A Outbox e a unica excecao, e ela e explicada: o pedido grava UM
        // evento, que e o gatilho do proprio trabalho. Nenhum evento
        // operacional aparece — se aparecesse, o worker de alertas o
        // transformaria em alerta e a fila do analista receberia dado de
        // ensaio.
        var eventosDepois = await EventosPorTipoAsync();

        Assert.Equal(
            (eventosAntes.GetValueOrDefault(TipoDoGatilho) + 1, eventosAntes.GetValueOrDefault(TipoOperacional)),
            (eventosDepois.GetValueOrDefault(TipoDoGatilho), eventosDepois.GetValueOrDefault(TipoOperacional)));
    }

    [Fact]
    public async Task O_rascunho_continua_rascunho_depois_do_backtest()
    {
        // Simular nao publica. Se publicasse, o Supervisor descobriria pela
        // fila de alertas.
        await IngerirLoteAsync("rascunho", quantidade: 2);

        var regra = await AbrirRascunhoDeVelocidadeAsync(maximo: 1, janela: 60, pontos: 50);

        await SolicitarAsync(regraId: regra);
        await _mensageria.RodarBacktestsAteEsvaziarAsync(Cancelamento);

        var administrada = await GetAsync($"{CaminhoDeRegras}/{regra}");

        Assert.NotEqual(JsonValueKind.Null, administrada.GetProperty("rascunho").ValueKind);
        Assert.Equal(1, administrada.GetProperty("numeroDaVersaoVigente").GetInt32());
        Assert.Equal(35, administrada.GetProperty("pontosVigentes").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Fila separada (CLAUDE.md secao 30)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_backtest_vai_para_a_fila_dedicada_e_o_worker_operacional_nao_o_consome()
    {
        await IngerirLoteAsync("fila", quantidade: 2);

        // Esvazia o que a ingestao produziu, para que a contagem seguinte
        // fale apenas do backtest.
        await _mensageria.RodarAteEsvaziarAsync(Cancelamento);

        var regra = await AbrirRascunhoDeVelocidadeAsync(maximo: 1, janela: 60, pontos: 50);
        var pedido = await SolicitarAsync(regraId: regra);

        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        Assert.Equal(
            1,
            await _mensageria.Fila.ContarPendentesAsync(
                Application.Mensageria.IFilaDeMensagens.FilaDeBacktests,
                Cancelamento));

        Assert.Equal(
            0,
            await _mensageria.Fila.ContarPendentesAsync(
                Application.Mensageria.IFilaDeMensagens.FilaOperacional,
                Cancelamento));

        // O consumidor operacional roda e nao encosta na mensagem.
        var operacional = await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(0, operacional.Total);
        Assert.Equal(nameof(StatusDoBacktest.Pendente), Status(await ObterAsync(Id(pedido))));

        // Só o consumidor dedicado a executa.
        await _mensageria.Backtests.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(nameof(StatusDoBacktest.Concluida), Status(await ObterAsync(Id(pedido))));
    }

    [Fact]
    public async Task Entrega_repetida_nao_reexecuta_o_backtest()
    {
        // O status da execucao faz o papel da Inbox: uma reentrega encontra
        // "Concluida" e nao faz nada.
        await IngerirLoteAsync("repete", quantidade: 3);

        var regra = await AbrirRascunhoDeVelocidadeAsync(maximo: 1, janela: 60, pontos: 50);
        var pedido = await SolicitarAsync(regraId: regra);

        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        var corpo = await CorpoDaFilaDeBacktestsAsync();

        var primeiro = await _mensageria.Backtests.ConsumirLoteAsync(Cancelamento);
        Assert.Equal(1, primeiro.Processados);

        var concluidoEm = (await ObterAsync(Id(pedido)))
            .GetProperty("execucao")
            .GetProperty("concluidaEm")
            .GetString();

        // A mesma mensagem, de novo.
        await _mensageria.Fila.EnviarAsync(
            Application.Mensageria.IFilaDeMensagens.FilaDeBacktests,
            corpo,
            Cancelamento);

        var segundo = await _mensageria.Backtests.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(0, segundo.Processados);
        Assert.Equal(1, segundo.Repetidos);

        // E o registro nao foi reescrito.
        Assert.Equal(
            concluidoEm,
            (await ObterAsync(Id(pedido)))
                .GetProperty("execucao")
                .GetProperty("concluidaEm")
                .GetString());
    }

    // -----------------------------------------------------------------------
    // Snapshot
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Editar_o_rascunho_depois_do_pedido_nao_muda_o_que_sera_simulado()
    {
        // O candidato e congelado no pedido. Se o worker lesse o rascunho na
        // hora de executar, o resultado descreveria uma configuracao diferente
        // da que foi pedida — e ninguem saberia disso olhando a tela.
        await IngerirLoteAsync("congela", quantidade: 3);

        var regra = await AbrirRascunhoDeVelocidadeAsync(maximo: 1, janela: 60, pontos: 90);
        var pedido = await SolicitarAsync(regraId: regra);

        var administrada = await GetAsync($"{CaminhoDeRegras}/{regra}");

        await SalvarRascunhoAsync(
            regra,
            maximo: 999,
            janela: 1,
            pontos: 1,
            versao: administrada.GetProperty("versao").GetInt32());

        await _mensageria.RodarBacktestsAteEsvaziarAsync(Cancelamento);

        var candidata = (await ObterAsync(Id(pedido)))
            .GetProperty("candidato")
            .GetProperty("regras")
            .EnumerateArray()
            .Single(r => r.GetProperty("regraId").GetGuid() == regra);

        Assert.Equal(90, candidata.GetProperty("pontos").GetInt32());
        Assert.Contains(
            "1 tentativa(s) em 60 minuto(s)",
            candidata.GetProperty("configuracao").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publicar_depois_do_pedido_nao_muda_a_base_de_comparacao()
    {
        // A comparacao e contra o perfil que valia quando a pergunta foi
        // feita. Sem congelar, "quantas decisoes mudariam" passaria a misturar
        // o efeito do candidato com o efeito de uma publicacao no meio.
        await IngerirLoteAsync("baseline", quantidade: 2);

        var perfilAntes = (await PerfilVigenteAsync()).GetProperty("numero").GetInt32();

        var regra = await AbrirRascunhoDeVelocidadeAsync(maximo: 2, janela: 30, pontos: 40);
        var pedido = await SolicitarAsync(regraId: regra);

        // Outra regra e publicada, sucedendo o perfil.
        var outra = await RegraPorTipoAsync(TipoDeRegra.NovoDispositivo);
        var outraId = outra.GetProperty("id").GetGuid();

        await SalvarRascunhoDeDispositivoAsync(
            outraId,
            minimo: 9,
            pontos: 22,
            versao: outra.GetProperty("versao").GetInt32());

        await PublicarAsync(outraId);

        Assert.True((await PerfilVigenteAsync()).GetProperty("numero").GetInt32() > perfilAntes);

        await _mensageria.RodarBacktestsAteEsvaziarAsync(Cancelamento);

        var execucao = (await ObterAsync(Id(pedido))).GetProperty("execucao");

        Assert.Equal(nameof(StatusDoBacktest.Concluida), execucao.GetProperty("status").GetString());
        Assert.Equal(
            perfilAntes,
            execucao.GetProperty("numeroDaVersaoDePerfilVigente").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Veredito humano (ROADMAP 9.6)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_veredito_da_investigacao_aparece_com_o_proprio_denominador()
    {
        // Um alerta vira caso, o caso e resolvido como legitima, e o backtest
        // consegue dizer: "de 1 transacao concluida como legitima, o candidato
        // faria X". Sem chamar isso de precisao — o denominador esta na tela.
        var alerta = await CenarioDeInvestigacao.GerarAlertaAsync(
            _cliente,
            _banco.StringDeConexao,
            _tenant,
            _integracao,
            _mensageria,
            "veredito",
            Cancelamento);

        await ResolverComoLegitimaAsync(alerta.Id);

        var pedido = await SolicitarAsync(regraId: null, limiarDeRevisao: 30, limiarDeBloqueio: 60);

        await _mensageria.RodarBacktestsAteEsvaziarAsync(Cancelamento);

        var linhas = (await ObterAsync(Id(pedido)))
            .GetProperty("resultado")
            .GetProperty("porVeredito")
            .EnumerateArray()
            .ToList();

        var legitima = linhas.Single(
            l => l.GetProperty("veredito").GetString() == nameof(ResultadoDaInvestigacao.Legitima));

        Assert.Equal(1, legitima.GetProperty("total").GetInt32());

        // E "sem resultado conhecido" e uma linha propria, com veredito nulo:
        // ausencia de investigacao nao e o mesmo que investigacao
        // inconclusiva.
        var desconhecido = linhas.Single(
            l => l.GetProperty("veredito").ValueKind == JsonValueKind.Null);

        Assert.True(desconhecido.GetProperty("total").GetInt32() > 0);
    }

    // -----------------------------------------------------------------------
    // Cancelamento e limites (ROADMAP 9.7)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cancelar_antes_da_execucao_impede_o_worker_de_rodar()
    {
        await IngerirLoteAsync("cancela", quantidade: 2);

        var regra = await AbrirRascunhoDeVelocidadeAsync(maximo: 1, janela: 60, pontos: 50);
        var pedido = await SolicitarAsync(regraId: regra);

        var cancelado = await CancelarAsync(
            Id(pedido),
            pedido.GetProperty("execucao").GetProperty("versao").GetInt32());

        Assert.Equal(nameof(StatusDoBacktest.Cancelada), Status(cancelado));

        await _mensageria.RodarBacktestsAteEsvaziarAsync(Cancelamento);

        var depois = await ObterAsync(Id(pedido));

        Assert.Equal(nameof(StatusDoBacktest.Cancelada), Status(depois));
        Assert.Equal(JsonValueKind.Null, depois.GetProperty("resultado").ValueKind);
    }

    [Fact]
    public async Task Cancelar_com_versao_velha_e_recusado()
    {
        var pedido = await SolicitarAsync(regraId: null, limiarDeRevisao: 35, limiarDeBloqueio: 65);

        using var resposta = await PostAsync(
            $"{Caminho}/{Id(pedido)}/cancelamento",
            new { versao = 999 });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("versao_desatualizada", await CodigoAsync(resposta));
    }

    [Fact]
    public async Task Cancelar_o_que_ja_concluiu_e_recusado()
    {
        await IngerirLoteAsync("tarde", quantidade: 2);

        var pedido = await SolicitarAsync(regraId: null, limiarDeRevisao: 35, limiarDeBloqueio: 65);
        await _mensageria.RodarBacktestsAteEsvaziarAsync(Cancelamento);

        var concluido = await ObterAsync(Id(pedido));

        using var resposta = await PostAsync(
            $"{Caminho}/{Id(pedido)}/cancelamento",
            new { versao = concluido.GetProperty("execucao").GetProperty("versao").GetInt32() });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("estado_do_backtest", await CodigoAsync(resposta));
    }

    [Fact]
    public async Task Janela_maior_que_o_limite_e_recusada()
    {
        using var resposta = await PostAsync(Caminho, new
        {
            regraId = (Guid?)null,
            limiarDeRevisao = (int?)null,
            limiarDeBloqueio = (int?)null,
            inicio = DateTimeOffset.UtcNow.AddDays(-400),
            fim = DateTimeOffset.UtcNow,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Janela_invertida_e_recusada()
    {
        using var resposta = await PostAsync(Caminho, new
        {
            regraId = (Guid?)null,
            limiarDeRevisao = (int?)null,
            limiarDeBloqueio = (int?)null,
            inicio = DateTimeOffset.UtcNow,
            fim = DateTimeOffset.UtcNow.AddDays(-1),
        });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Regra_sem_rascunho_nao_pode_ser_simulada()
    {
        // Sem rascunho o candidato seria identico ao vigente: uma execucao
        // inteira para dizer "nada muda".
        var regra = await RegraPorTipoAsync(TipoDeRegra.DivergenciaGeografica);

        using var resposta = await PostAsync(Caminho, Pedido(regra.GetProperty("id").GetGuid()));

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("sem_rascunho", await CodigoAsync(resposta));
    }

    [Fact]
    public async Task Regra_desativada_nao_pode_ser_simulada()
    {
        var regra = await RegraPorTipoAsync(TipoDeRegra.NovoDispositivo);
        var regraId = regra.GetProperty("id").GetGuid();

        await SalvarRascunhoDeDispositivoAsync(
            regraId,
            minimo: 8,
            pontos: 25,
            versao: regra.GetProperty("versao").GetInt32());

        var comRascunho = await GetAsync($"{CaminhoDeRegras}/{regraId}");

        using var desativacao = await PostAsync(
            $"{CaminhoDeRegras}/{regraId}/ativacao",
            new { ativa = false, versao = comRascunho.GetProperty("versao").GetInt32() });

        desativacao.EnsureSuccessStatusCode();

        using var resposta = await PostAsync(Caminho, Pedido(regraId));

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("regra_desativada", await CodigoAsync(resposta));
    }

    [Fact]
    public async Task Uma_organizacao_nao_pode_empilhar_execucoes_sem_limite()
    {
        // Defesa contra spam: sem ela, um clique repetido enfileira trabalho
        // ilimitado na fila que existe justamente para nao atrapalhar a
        // operacao.
        for (var i = 0; i < 2; i++)
        {
            using var aceita = await PostAsync(Caminho, PedidoDeLimiares(30 + i, 60 + i));
            Assert.Equal(HttpStatusCode.Accepted, aceita.StatusCode);
        }

        using var terceira = await PostAsync(Caminho, PedidoDeLimiares(38, 68));

        Assert.Equal(HttpStatusCode.Conflict, terceira.StatusCode);
        Assert.Equal("limite_de_execucoes", await CodigoAsync(terceira));

        // Depois que as duas terminam, a vaga volta.
        await _mensageria.RodarBacktestsAteEsvaziarAsync(Cancelamento);

        using var quarta = await PostAsync(Caminho, PedidoDeLimiares(38, 68));

        Assert.Equal(HttpStatusCode.Accepted, quarta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Listagem
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_listagem_traz_as_execucoes_da_mais_recente_para_a_mais_antiga()
    {
        var primeira = Id(await SolicitarAsync(regraId: null, 31, 61));
        var segunda = Id(await SolicitarAsync(regraId: null, 32, 62));

        var pagina = await GetAsync(Caminho);

        var ids = pagina
            .GetProperty("itens")
            .EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();

        Assert.Equal(2, pagina.GetProperty("totalDeItens").GetInt64());
        Assert.Equal([segunda, primeira], ids);

        // A listagem nao carrega o candidato: ela e feita para varrer, e o
        // snapshot so interessa a quem abre o detalhe.
        Assert.False(pagina.GetProperty("itens")[0].TryGetProperty("candidato", out _));
    }

    [Fact]
    public async Task Backtest_inexistente_responde_404()
    {
        using var resposta = await GetBrutoAsync($"{Caminho}/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    // =======================================================================
    // Apoio
    // =======================================================================

    private static string Status(JsonElement detalhe) =>
        detalhe.GetProperty("execucao").GetProperty("status").GetString()!;

    private static Guid Id(JsonElement detalhe) =>
        detalhe.GetProperty("execucao").GetProperty("id").GetGuid();

    private static object Pedido(Guid? regraId, int? revisao = null, int? bloqueio = null) => new
    {
        regraId,
        limiarDeRevisao = revisao,
        limiarDeBloqueio = bloqueio,
        inicio = DateTimeOffset.UtcNow.AddDays(-30),
        fim = DateTimeOffset.UtcNow.AddMinutes(5),
    };

    private static object PedidoDeLimiares(int revisao, int bloqueio) =>
        Pedido(null, revisao, bloqueio);

    private async Task<JsonElement> SolicitarAsync(
        Guid? regraId,
        int? limiarDeRevisao = null,
        int? limiarDeBloqueio = null)
    {
        using var resposta = await PostAsync(
            Caminho,
            Pedido(regraId, limiarDeRevisao, limiarDeBloqueio));

        Assert.Equal(HttpStatusCode.Accepted, resposta.StatusCode);

        return await LerAsync(resposta);
    }

    private Task<JsonElement> ObterAsync(Guid execucaoId) => GetAsync($"{Caminho}/{execucaoId}");

    private async Task<JsonElement> CancelarAsync(Guid execucaoId, int versao)
    {
        using var resposta = await PostAsync($"{Caminho}/{execucaoId}/cancelamento", new { versao });

        resposta.EnsureSuccessStatusCode();

        return await LerAsync(resposta);
    }

    /// <summary>
    /// Abre um rascunho na regra de velocidade e devolve o identificador dela.
    ///
    /// A regra ja nasce publicada com o catalogo padrao, entao aqui so se
    /// escreve o rascunho — que e exatamente o que o backtest simula.
    /// </summary>
    private async Task<Guid> AbrirRascunhoDeVelocidadeAsync(int maximo, int janela, int pontos)
    {
        var regra = await RegraPorTipoAsync(TipoDeRegra.VelocidadePorCliente);
        var regraId = regra.GetProperty("id").GetGuid();

        await SalvarRascunhoAsync(
            regraId,
            maximo,
            janela,
            pontos,
            regra.GetProperty("versao").GetInt32());

        return regraId;
    }

    private async Task SalvarRascunhoAsync(Guid regraId, int maximo, int janela, int pontos, int versao)
    {
        var token = await TokenAsync(PerfilDeUsuario.SupervisorDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Put,
            $"{CaminhoDeRegras}/{regraId}/rascunho",
            token);

        requisicao.Content = JsonContent.Create(new
        {
            nome = "Velocidade por cliente",
            configuracao = new Dictionary<string, decimal>
            {
                ["maximoDeTransacoes"] = maximo,
                ["janelaEmMinutos"] = janela,
            },
            pontos,
            versao,
        });

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        resposta.EnsureSuccessStatusCode();
    }

    private async Task SalvarRascunhoDeDispositivoAsync(
        Guid regraId,
        int minimo,
        int pontos,
        int versao)
    {
        var token = await TokenAsync(PerfilDeUsuario.SupervisorDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Put,
            $"{CaminhoDeRegras}/{regraId}/rascunho",
            token);

        requisicao.Content = JsonContent.Create(new
        {
            nome = "Dispositivo novo",
            configuracao = new Dictionary<string, decimal>
            {
                ["minimoDeTransacoesNoHistorico"] = minimo,
            },
            pontos,
            versao,
        });

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        resposta.EnsureSuccessStatusCode();
    }

    private async Task PublicarAsync(Guid regraId)
    {
        var atual = await GetAsync($"{CaminhoDeRegras}/{regraId}");

        using var resposta = await PostAsync(
            $"{CaminhoDeRegras}/{regraId}/publicacao",
            new { versao = atual.GetProperty("versao").GetInt32() });

        resposta.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> RegraPorTipoAsync(TipoDeRegra tipo)
    {
        var regras = await GetAsync($"{CaminhoDeRegras}/gestao");

        return regras.EnumerateArray().First(r => r.GetProperty("tipo").GetString() == tipo.ToString());
    }

    private Task<JsonElement> PerfilVigenteAsync() => GetAsync($"{CaminhoDeRegras}/perfil");

    /// <summary>
    /// Ingere transacoes do mesmo cliente, espacadas em minutos.
    ///
    /// Espacadas de proposito: e o que faz a regra de velocidade candidata
    /// acionar em algumas e nao em todas, dando ao backtest algo para comparar.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> IngerirLoteAsync(string prefixo, int quantidade)
    {
        var cliente = $"cli-{prefixo}-{Guid.NewGuid():N}"[..24];
        var inicio = DateTimeOffset.UtcNow.AddMinutes(-quantidade * 3);
        var ids = new List<Guid>();

        for (var i = 0; i < quantidade; i++)
        {
            using var resposta = await CenarioDeIngestao.EnviarAsync(
                _cliente,
                _integracao.Chave,
                $"idem-{Guid.NewGuid():N}",
                CenarioDeIngestao.Corpo(
                    identificadorExterno: $"{prefixo}-{i}-{Guid.NewGuid():N}"[..24],
                    valor: 100m,
                    clienteExternoId: cliente,
                    ocorridaEm: inicio.AddMinutes(i * 3),
                    fingerprintDoDispositivo: "disp-de-casa",
                    paisDeOrigem: "BR"),
                Cancelamento);

            resposta.EnsureSuccessStatusCode();

            ids.Add(await CenarioDeIngestao.IdDaTransacaoAsync(resposta));
        }

        return ids;
    }

    /// <summary>As decisoes que o motor de verdade gravou, agrupadas.</summary>
    private async Task<IReadOnlyDictionary<Decisao, int>> DecisoesGravadasAsync(
        IReadOnlyList<Guid> transacoes)
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var avaliacoes = await contexto.AvaliacoesDeRisco
            .AsNoTracking()
            .Where(a => transacoes.Contains(a.TransacaoId))
            .Select(a => a.Decisao)
            .ToListAsync(Cancelamento);

        return avaliacoes
            .GroupBy(d => d)
            .ToDictionary(grupo => grupo.Key, grupo => grupo.Count());
    }

    /// <summary>
    /// Contagem de tudo o que um backtest nao pode tocar.
    ///
    /// Conferida antes e depois. Uma lista, e nao um teste por tabela: o que
    /// precisa ser afirmado e "nada mudou", e enumerar as tabelas em um lugar
    /// so faz a proxima tabela do produto entrar aqui junto.
    /// </summary>
    private async Task<IReadOnlyList<int>> ContagensDeProducaoAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        return
        [
            await contexto.Transacoes.CountAsync(Cancelamento),
            await contexto.AvaliacoesDeRisco.CountAsync(Cancelamento),
            await contexto.Set<SinalDeRisco>().CountAsync(Cancelamento),
            await contexto.Alertas.CountAsync(Cancelamento),
            await contexto.Casos.CountAsync(Cancelamento),
            await contexto.VersoesDeRegra.CountAsync(Cancelamento),
            await contexto.VersoesDePerfilDeRisco.CountAsync(Cancelamento),
            await contexto.ResultadosDeInvestigacao.CountAsync(Cancelamento),
        ];
    }

    private const string TipoDoGatilho = "BacktestSolicitado.v1";
    private const string TipoOperacional = "TransacaoAvaliada.v1";

    /// <summary>Quantos eventos de cada tipo o tenant tem na Outbox.</summary>
    private async Task<IReadOnlyDictionary<string, int>> EventosPorTipoAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        return await contexto.EventosDeSaida
            .AsNoTracking()
            .GroupBy(e => e.Tipo)
            .Select(grupo => new { Tipo = grupo.Key, Quantidade = grupo.Count() })
            .ToDictionaryAsync(x => x.Tipo, x => x.Quantidade, Cancelamento);
    }

    private async Task<string> CorpoDaFilaDeBacktestsAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        return await contexto.FilaDeMensagens
            .AsNoTracking()
            .Where(m => m.Fila == Application.Mensageria.IFilaDeMensagens.FilaDeBacktests)
            .Select(m => m.Corpo)
            .FirstAsync(Cancelamento);
    }

    private async Task ResolverComoLegitimaAsync(Guid alertaId)
    {
        var caso = await LerAsync(await PostAsync(
            "/api/casos",
            new { titulo = "Investigacao do backtest", alertasIds = new[] { alertaId } },
            PerfilDeUsuario.AnalistaDeFraude));

        var casoId = caso.GetProperty("id").GetGuid();

        var assumido = await LerAsync(await PostAsync(
            $"/api/casos/{casoId}/assumir",
            new { versao = caso.GetProperty("versao").GetInt32() },
            PerfilDeUsuario.AnalistaDeFraude));

        using var resolvido = await PostAsync(
            $"/api/casos/{casoId}/resolucao",
            new
            {
                resultado = nameof(ResultadoDaInvestigacao.Legitima),
                versao = assumido.GetProperty("versao").GetInt32(),
            },
            PerfilDeUsuario.AnalistaDeFraude);

        resolvido.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> PostAsync(
        string caminho,
        object corpo,
        PerfilDeUsuario perfil = PerfilDeUsuario.SupervisorDeFraude)
    {
        var token = await TokenAsync(perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Post, caminho, token);
        requisicao.Content = JsonContent.Create(corpo);

        return await _cliente.SendAsync(requisicao, Cancelamento);
    }

    private async Task<HttpResponseMessage> GetBrutoAsync(
        string caminho,
        PerfilDeUsuario perfil = PerfilDeUsuario.SupervisorDeFraude)
    {
        var token = await TokenAsync(perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, caminho, token);

        return await _cliente.SendAsync(requisicao, Cancelamento);
    }

    private async Task<JsonElement> GetAsync(
        string caminho,
        PerfilDeUsuario perfil = PerfilDeUsuario.SupervisorDeFraude)
    {
        using var resposta = await GetBrutoAsync(caminho, perfil);

        resposta.EnsureSuccessStatusCode();

        return await LerAsync(resposta);
    }

    private async Task<string> TokenAsync(PerfilDeUsuario perfil)
    {
        if (_tokens.TryGetValue(perfil, out var existente))
        {
            return existente;
        }

        var token = await CenarioDeIngestao.TokenDeAsync(_cliente, _tenant, perfil, Cancelamento);
        _tokens[perfil] = token;

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
