using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Application.Observabilidade;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Infrastructure.Mensageria;
using CentralAntifraude.Infrastructure.Observabilidade;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// O sistema consegue ser operado e diagnosticado? (ROADMAP 12.1)
///
/// A pergunta desta fase nao e "o produto funciona", que dez fases ja
/// responderam. E outra, e ela so pode ser feita depois: **quando algo der
/// errado as tres da manha, existe como descobrir o que foi?**
///
/// O fio de correlacao e a resposta, e ele tem um ponto fraco conhecido: a
/// fronteira entre o sincrono e o assincrono. De um lado ha uma requisicao HTTP
/// com escopo, identidade e contexto; do outro, um laco de fundo que acorda,
/// pega uma linha de uma tabela e age. **A Fase 5 ja provava que o
/// identificador atravessa o BANCO** — ele esta na Outbox, no envelope e na
/// Inbox. O que faltava era o log: as linhas escritas pelo worker nasciam sem
/// correlacao nenhuma, e uma investigacao que seguisse o identificador chegava
/// ate a publicacao e recomecava do zero do outro lado.
///
/// Por isso o teste central desta classe le LOG, e nao banco: o que ele afirma
/// e justamente o que o banco nao pode provar.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class ObservabilidadeTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;
    private ColetorDeMetricas _metricas = null!;

    public ObservabilidadeTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"obs-{Guid.NewGuid():N}"[..14],
            Cancelamento);

        await CenarioDeMensageria.LimparAsync(_banco.StringDeConexao, Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();
        _integracao = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenant, Cancelamento);

        // Ligado depois do preparo: o que interessa e o que o cenario emite, e
        // nao o custo de montar o cenario.
        _metricas = new ColetorDeMetricas();
    }

    public async ValueTask DisposeAsync()
    {
        _metricas?.Dispose();
        _cliente?.Dispose();

        if (_fabrica is not null)
        {
            await _fabrica.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // 12.3 — Correlacao ponta a ponta
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_mesmo_identificador_de_correlacao_aparece_nos_quatro_elos_do_log()
    {
        var correlacao = $"corr-{Guid.NewGuid():N}"[..24];

        using (var resposta = await IngerirAsync("obs-fio", correlacao, score: true))
        {
            resposta.EnsureSuccessStatusCode();
        }

        // Despachante e worker sao resolvidos do container REAL da API, e nao
        // montados a mao: e o unico jeito de as linhas que eles escrevem
        // passarem pelo provedor de log do host — e, portanto, de o teste ver o
        // que producao veria.
        await RodarCicloAssincronoAsync();

        var linhas = _fabrica.Logs.Entradas
            .Where(e => string.Equals(e.Propriedade("CorrelationId"), correlacao, StringComparison.Ordinal))
            .ToList();

        var eventos = linhas.Select(l => l.EventId).ToHashSet();

        // Elo 1 — a requisicao HTTP.
        Assert.Contains(100, eventos);

        // Elo 2 — a publicacao na fila, que so existe porque a Fase 12 repos a
        // correlacao num escopo de log do despachante.
        Assert.Contains(502, eventos);

        // Elo 3 — o efeito aplicado pelo worker, em outro escopo de execucao.
        Assert.Contains(514, eventos);

        // Elo 4 — o alerta criado. Esta linha nunca teve escopo proprio: ela
        // herda a correlacao do escopo aberto pelo consumidor, que e o
        // comportamento que se quer garantir para todo efeito FUTURO tambem.
        Assert.Contains(610, eventos);
    }

    [Fact]
    public async Task O_worker_carrega_o_evento_e_o_tipo_junto_da_correlacao()
    {
        var correlacao = $"corr-{Guid.NewGuid():N}"[..24];

        using (var resposta = await IngerirAsync("obs-escopo", correlacao))
        {
            resposta.EnsureSuccessStatusCode();
        }

        await RodarCicloAssincronoAsync();

        var efeito = Assert.Single(
            _fabrica.Logs.Entradas,
            e => e.EventId == 514 &&
                 string.Equals(e.Propriedade("CorrelationId"), correlacao, StringComparison.Ordinal));

        // Correlacao responde "que operacao foi essa?"; EventId responde "qual
        // mensagem, entre as varias daquela operacao?". Uma sem a outra deixa
        // a investigacao pela metade quando um pedido gera mais de um evento.
        Assert.False(string.IsNullOrWhiteSpace(efeito.Propriedade("EventId")));
        Assert.Equal("TransacaoAvaliada.v1", efeito.Propriedade("EventType"));
    }

    // -----------------------------------------------------------------------
    // 12.2 — A linha da requisicao
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_requisicao_registra_operacao_status_e_duracao()
    {
        using (var resposta = await IngerirAsync("obs-linha"))
        {
            resposta.EnsureSuccessStatusCode();
        }

        var linha = Assert.Single(
            _fabrica.Logs.Entradas,
            e => e.EventId == 100 &&
                 string.Equals(
                     e.Propriedade("Operacao"),
                     $"POST {CenarioDeIngestao.CaminhoDaIngestao}",
                     StringComparison.Ordinal));

        Assert.Equal("201", linha.Propriedade("Status"));
        Assert.True(double.TryParse(
            linha.Propriedade("DuracaoEmMs"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var duracao));
        Assert.True(duracao >= 0);
    }

    [Fact]
    public async Task A_operacao_registrada_e_o_padrao_da_rota_e_nunca_o_caminho_com_identificador()
    {
        // O caso nao existe de proposito: a resposta vem de uma excecao de
        // dominio, e o tratador de excecoes limpa o endpoint antes de escrever
        // o erro. Se o middleware lesse apenas `GetEndpoint()`, esta linha
        // sairia como "desconhecida" — e a operacao se perderia exatamente nas
        // requisicoes que mais interessam a uma investigacao.
        var token = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenant,
            PerfilDeUsuario.AnalistaDeFraude,
            Cancelamento);

        var idInexistente = Guid.NewGuid();

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Get,
            $"/api/casos/{idInexistente}",
            token);

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resposta.StatusCode);

        var linha = Assert.Single(
            _fabrica.Logs.Entradas,
            e => e.EventId == 100 &&
                 (e.Propriedade("Operacao")?.StartsWith("GET /api/casos/", StringComparison.Ordinal) ?? false));

        // O padrao, e nao o caminho. Duas razoes, e a segunda pesa mais: como
        // dimensao de metrica o caminho criaria uma serie por caso investigado;
        // como texto de log ele carrega identificador de recurso de um tenant
        // para dentro de um sistema que nao tem tenant nem autorizacao.
        //
        // A afirmacao e sobre a FORMA — ha um parametro no lugar do valor —, e
        // nao sobre a sintaxe exata da restricao de rota: acoplar o teste a
        // `{id:guid}` o quebraria numa troca de restricao que nao muda nada do
        // que ele protege.
        var operacao = linha.Propriedade("Operacao");

        Assert.Contains("{id", operacao, StringComparison.Ordinal);
        Assert.DoesNotContain(idInexistente.ToString(), operacao, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(idInexistente.ToString(), linha.Mensagem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_saude_nao_entra_no_log_de_requisicao()
    {
        using (var resposta = await _cliente.GetAsync(new Uri("/health/live", UriKind.Relative), Cancelamento))
        {
            resposta.EnsureSuccessStatusCode();
        }

        // Um orquestrador consulta liveness a cada poucos segundos, para
        // sempre. Registrar isso encheria o log de linhas que ninguem le e
        // daria a distribuicao de latencia da API o formato do health check.
        Assert.DoesNotContain(
            _fabrica.Logs.Entradas,
            e => e.EventId == 100 &&
                 (e.Propriedade("Operacao")?.Contains("health", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    [Fact]
    public async Task Uma_url_sem_rota_vira_operacao_desconhecida_e_nao_uma_serie_nova()
    {
        var caminho = $"/api/{Guid.NewGuid():N}/wp-admin";

        using var requisicao = new HttpRequestMessage(HttpMethod.Get, new Uri(caminho, UriKind.Relative));
        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        // 401, e nao 404: a politica de autorizacao padrao e fechada, entao uma
        // rota inexistente nem chega a ser procurada. Comportamento da Fase 11.
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resposta.StatusCode);

        // Um varredor de vulnerabilidade tenta milhares de URLs diferentes. Se
        // a operacao fosse o caminho recebido, cada tentativa viraria uma serie
        // de metrica nova — a varredura sairia mais cara para quem e varrido do
        // que para quem varre.
        var linha = Assert.Single(
            _fabrica.Logs.Entradas,
            e => e.EventId == 100 &&
                 string.Equals(e.Propriedade("Operacao"), "desconhecida", StringComparison.Ordinal));

        // 401 e o contrato funcionando. Registrar isso como aviso treinaria
        // qualquer pessoa a ignorar avisos — que e como um aviso de verdade
        // passa despercebido.
        Assert.Equal(LogLevel.Information, linha.Nivel);
        Assert.DoesNotContain(_fabrica.Logs.Entradas, e => e.EventId == 101);
    }

    // -----------------------------------------------------------------------
    // 12.4 — Metricas
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_caminho_critico_e_o_assincrono_emitem_as_metricas_do_catalogo()
    {
        using (var resposta = await IngerirAsync("obs-metrica", score: true))
        {
            resposta.EnsureSuccessStatusCode();
        }

        await RodarCicloAssincronoAsync();

        // Nao se afirma valor exato: outras classes de teste rodam no mesmo
        // processo e compartilham os instrumentos, que sao estaticos. O que
        // este teste garante e que cada instrumento do catalogo EXISTE e e
        // alimentado pelo fluxo real — uma metrica declarada e nunca emitida e
        // pior do que nenhuma, porque um painel a mostra como zero.
        var esperados = new[]
        {
            Telemetria.Instrumentos.RequisicoesTotal,
            Telemetria.Instrumentos.RequisicaoDuracao,
            Telemetria.Instrumentos.AvaliacoesTotal,
            Telemetria.Instrumentos.AvaliacaoDuracao,
            Telemetria.Instrumentos.DecisoesTotal,
            Telemetria.Instrumentos.OutboxPublicados,
            Telemetria.Instrumentos.MensagensConsumidas,
        };

        var emitidos = _metricas.InstrumentosUsados();

        Assert.All(esperados, nome => Assert.Contains(nome, emitidos));

        Assert.True(_metricas.Quantidade(Telemetria.Instrumentos.AvaliacaoDuracao) >= 1);
        Assert.True(_metricas.Soma(Telemetria.Instrumentos.MensagensConsumidas, "resultado", "Aplicado") >= 1);
    }

    [Fact]
    public async Task Nenhuma_metrica_carrega_dimensao_fora_da_lista_permitida()
    {
        using (var resposta = await IngerirAsync("obs-cardinalidade", score: true))
        {
            resposta.EnsureSuccessStatusCode();
        }

        await RodarCicloAssincronoAsync();
        await AmostrarIndicadoresAsync();

        var usadas = _metricas.DimensoesUsadas();

        // Este e o teste que protege a PROXIMA metrica, e nao as de hoje.
        // Acrescentar `tenantId` como dimensao e a coisa mais natural do mundo
        // — o painel fica melhor, a serie fica util — e tambem a mais cara:
        // uma serie por organizacao, multiplicada por cada valor das outras
        // dimensoes (CLAUDE.md secao 71). Aqui isso quebra a build, e quem
        // quiser a dimensao precisa declarar quantos valores ela pode ter.
        Assert.All(
            usadas,
            dimensao => Assert.Contains(dimensao, Telemetria.DimensoesPermitidas));
    }

    [Fact]
    public async Task A_profundidade_da_outbox_e_a_idade_do_atraso_sao_medidas()
    {
        using (var resposta = await IngerirAsync("obs-profundidade"))
        {
            resposta.EnsureSuccessStatusCode();
        }

        // Amostra ANTES de despachar: o evento esta pendente, e e esse o
        // estado que a metrica precisa enxergar.
        await AmostrarIndicadoresAsync();

        Assert.True(_metricas.Ultimo(Telemetria.Instrumentos.OutboxPendentes) >= 1);

        // A idade e a metrica que distingue "pico de trafego" de "despachante
        // parado". Uma Outbox com tres eventos pendentes ha quarenta minutos e
        // um problema; com trezentos ha dois segundos, nao e.
        Assert.NotNull(_metricas.Ultimo(Telemetria.Instrumentos.OutboxIdade));

        Assert.NotNull(_metricas.Ultimo(
            Telemetria.Instrumentos.FilaPendentes,
            "fila",
            IFilaDeMensagens.FilaOperacional));
    }

    [Fact]
    public async Task A_amostragem_respeita_o_intervalo_minimo_em_vez_de_medir_a_cada_ciclo()
    {
        // Os lacos rodam a cada 100 ms quando ha trabalho. Medir profundidade
        // nesse ritmo seriam dezenas de consultas por segundo para responder
        // uma pergunta que muda em minutos (CLAUDE.md secao 78).
        using var escopo = _fabrica.Services.CreateScope();
        var amostrador = escopo.ServiceProvider.GetRequiredService<AmostradorDeIndicadores>();

        await amostrador.AmostrarSeVencidoAsync(Cancelamento);

        var segunda = await amostrador.AmostrarSeVencidoAsync(Cancelamento);

        Assert.False(segunda);
    }

    // -----------------------------------------------------------------------
    // 12.2 — O que NAO pode entrar no log
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_log_nao_carrega_credencial_token_nem_conteudo_do_evento()
    {
        var token = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenant,
            PerfilDeUsuario.Administrador,
            Cancelamento);

        using (var resposta = await IngerirAsync("obs-segredo", score: true))
        {
            resposta.EnsureSuccessStatusCode();
        }

        await RodarCicloAssincronoAsync();

        var tudo = string.Join('\n', _fabrica.Logs.Entradas.Select(e =>
            e.Mensagem + '\n' + string.Join('\n', e.Propriedades.Select(p => $"{p.Key}={p.Value}"))));

        // A chave da integracao e o access token sao credenciais: nenhuma parte
        // delas pode aparecer, nem truncada (CLAUDE.md secoes 50 e 56).
        Assert.DoesNotContain(_integracao.Chave, tudo, StringComparison.Ordinal);
        Assert.DoesNotContain(token, tudo, StringComparison.Ordinal);

        // O corpo do evento carrega score, decisao e identificador de cliente.
        // Ele fica na tabela, sob autorizacao — e nao no log, que sai do
        // perimetro do banco e vive sob outra politica de acesso.
        Assert.DoesNotContain("\"score\"", tudo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cli-obs-segredo", tudo, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Auxiliares
    // -----------------------------------------------------------------------

    /// <summary>
    /// Despacha e consome usando a composicao real da API.
    ///
    /// Cada etapa em seu proprio escopo, como os lacos de fundo fazem: um
    /// escopo unico manteria o mesmo <c>DbContext</c> — e o mesmo portador de
    /// correlacao — vivo entre as duas, o que faria o teste passar por um
    /// motivo que producao nao reproduz.
    /// </summary>
    private async Task RodarCicloAssincronoAsync()
    {
        using (var escopo = _fabrica.Services.CreateScope())
        {
            await escopo.ServiceProvider
                .GetRequiredService<DespachanteDeEventos>()
                .DespacharLoteAsync(Cancelamento);
        }

        using (var escopo = _fabrica.Services.CreateScope())
        {
            await escopo.ServiceProvider
                .GetRequiredService<ProcessadorDeEventos>()
                .ConsumirLoteAsync(Cancelamento);
        }
    }

    private async Task AmostrarIndicadoresAsync()
    {
        using var escopo = _fabrica.Services.CreateScope();

        await escopo.ServiceProvider
            .GetRequiredService<AmostradorDeIndicadores>()
            .AmostrarAsync(Cancelamento);
    }

    /// <summary>
    /// Uma transacao. Com <paramref name="score"/>, o cenario completo que
    /// aciona regra e produz alerta.
    ///
    /// O historico e obrigatorio: "dispositivo novo" e "pais divergente" so
    /// significam alguma coisa contra um passado. Uma transacao isolada de um
    /// cliente desconhecido nao aciona regra nenhuma — e um teste que esperasse
    /// alerta a partir dela estaria errado sobre o produto, e nao sobre o log.
    /// </summary>
    private async Task<HttpResponseMessage> IngerirAsync(
        string identificador,
        string? correlacao = null,
        bool score = false)
    {
        var cliente = $"cli-{identificador}";

        if (score)
        {
            // Espacado em dias: em minutos, a regra de velocidade tambem
            // acionaria e o cenario deixaria de ser o que ele afirma ser.
            for (var i = 0; i < 3; i++)
            {
                using var historico = await EnviarAsync(
                    $"{identificador}-hist-{i}",
                    cliente,
                    DateTimeOffset.UtcNow.AddDays(-10 + i),
                    "disp-de-casa",
                    "BR",
                    valor: 100m,
                    correlacao: null);

                historico.EnsureSuccessStatusCode();
            }
        }

        return await EnviarAsync(
            identificador,
            cliente,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            score ? "disp-novo" : "disp-de-casa",
            score ? "PT" : "BR",
            valor: score ? 900m : 100m,
            correlacao: correlacao);
    }

    private Task<HttpResponseMessage> EnviarAsync(
        string identificador,
        string cliente,
        DateTimeOffset ocorridaEm,
        string dispositivo,
        string pais,
        decimal valor,
        string? correlacao)
    {
        var requisicao = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(CenarioDeIngestao.CaminhoDaIngestao, UriKind.Relative))
        {
            Content = System.Net.Http.Json.JsonContent.Create(CenarioDeIngestao.Corpo(
                identificadorExterno: identificador,
                valor: valor,
                clienteExternoId: cliente,
                ocorridaEm: ocorridaEm,
                fingerprintDoDispositivo: dispositivo,
                paisDeOrigem: pais)),
        };

        requisicao.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{CenarioDeIngestao.EsquemaDaChave} {_integracao.Chave}");

        requisicao.Headers.TryAddWithoutValidation(
            CenarioDeIngestao.CabecalhoDeIdempotencia,
            $"idem-{Guid.NewGuid():N}");

        if (correlacao is not null)
        {
            requisicao.Headers.TryAddWithoutValidation("X-Correlation-Id", correlacao);
        }

        return _cliente.SendAsync(requisicao, Cancelamento);
    }
}
