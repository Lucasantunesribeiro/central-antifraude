using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Infrastructure.Mensageria;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// O caminho assincrono inteiro: ingestao, Outbox, fila, worker, efeito.
///
/// **O que esta fase acrescenta e uma promessa modesta e dificil**: o efeito
/// acontece **uma vez**, mesmo com entrega repetida, processo caindo no meio e
/// varios despachantes ao mesmo tempo. Nao ha promessa de entrega unica — essa
/// ninguem consegue cumprir. Ha promessa de EFEITO unico.
///
/// O efeito escolhido e um contador, e a escolha e deliberada: somar duas
/// vezes nao deixa rastro nenhum. Um efeito com chave unica passaria mesmo com
/// a Inbox desligada, porque o banco seguraria a duplicata. O contador nao
/// perdoa.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class BackboneAssincronoTests : IAsyncLifetime
{
    private const string Fila = IFilaDeMensagens.FilaOperacional;

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;
    private CenarioDeMensageria _mensageria = null!;

    public BackboneAssincronoTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"async-{Guid.NewGuid():N}"[..14],
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
    // O caminho feliz, ponta a ponta
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Da_ingestao_ao_efeito_o_evento_atravessa_o_sistema_inteiro()
    {
        using var resposta = await IngerirAsync("ponta-a-ponta");
        resposta.EnsureSuccessStatusCode();

        // Antes do despacho, o evento existe e esta pendente. Este e o estado
        // "ja decidi, ainda nao avisei" — e ele precisa ser recuperavel.
        Assert.Equal(1, await PendentesNaOutboxAsync());
        Assert.Equal(0, await _mensageria.Fila.ContarPendentesAsync(Fila, Cancelamento));

        var despacho = await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        Assert.Equal(1, despacho.Publicados);
        Assert.Equal(0, await PendentesNaOutboxAsync());
        Assert.Equal(1, await _mensageria.Fila.ContarPendentesAsync(Fila, Cancelamento));

        var consumo = await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(1, consumo.Processados);
        Assert.Equal(0, await _mensageria.Fila.ContarPendentesAsync(Fila, Cancelamento));

        Assert.Equal(1, await ContagemNoResumoAsync(Decisao.Permitir));
    }

    [Fact]
    public async Task A_correlacao_da_requisicao_chega_ate_o_efeito()
    {
        // CLAUDE.md secao 69: HTTP, dominio, outbox, mensagem, worker, efeito.
        // Cada elo e verificado explicitamente — um teste que so olhasse as
        // pontas passaria mesmo com o fio partido no meio.
        var correlacao = $"corr-{Guid.NewGuid():N}"[..24];

        using var resposta = await IngerirAsync("com-correlacao", correlacao);
        resposta.EnsureSuccessStatusCode();

        // Elo 1: a resposta HTTP devolve a correlacao que o cliente enviou.
        Assert.Equal(
            correlacao,
            resposta.Headers.GetValues("X-Correlation-Id").Single());

        await using (var leitura = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId))
        {
            // Elo 2: a linha da Outbox.
            var evento = Assert.Single(await leitura.EventosDeSaida.ToListAsync(Cancelamento));
            Assert.Equal(correlacao, evento.IdDeCorrelacao);
        }

        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        // Elo 3: o corpo da mensagem na fila.
        var mensagem = Assert.Single(await _mensageria.Fila.ReceberAsync(Fila, 10, Cancelamento));
        Assert.Contains($"\"correlationId\":\"{correlacao}\"", mensagem.Corpo, StringComparison.Ordinal);

        await _mensageria.Fila.DevolverAsync(mensagem.Recibo, Cancelamento);
        await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        // Elo 4: o registro do efeito, no banco. Gravado, e nao so logado: o
        // log some com a retencao e a pergunta continua valendo depois disso.
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        // Cada manipulador deixa a propria marca: a chave da Inbox e
        // (consumidor, evento), e nao so o evento.
        var processados = await contexto.EventosProcessados.ToListAsync(Cancelamento);

        Assert.Equal(
            [CriadorDeAlertas.NomeDoConsumidor, ProjecaoDeDecisoesDiarias.NomeDoConsumidor],
            processados.Select(p => p.Consumidor).Order(StringComparer.Ordinal));

        Assert.All(processados, p => Assert.Equal(correlacao, p.IdDeCorrelacao));
    }

    [Fact]
    public async Task O_envelope_na_fila_carrega_o_contrato_completo()
    {
        // ROADMAP 5.3: eventId, eventType, version, tenantId, correlationId,
        // occurredAt e payload versionado. Tipo e versao SEPARADOS, para que o
        // consumidor decida se sabe ler antes de tentar interpretar.
        using var resposta = await IngerirAsync("envelope");
        resposta.EnsureSuccessStatusCode();

        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        var mensagem = Assert.Single(await _mensageria.Fila.ReceberAsync(Fila, 10, Cancelamento));
        var envelope = SerializadorDeEnvelope.Desserializar(mensagem.Corpo);

        Assert.NotEqual(Guid.Empty, envelope.EventId);
        Assert.Equal("TransacaoAvaliada", envelope.EventType);
        Assert.Equal(1, envelope.Version);
        Assert.Equal(_tenant.OrganizacaoId, envelope.TenantId);
        Assert.NotEmpty(envelope.CorrelationId);
        Assert.Equal(TransacaoAvaliadaV1.NomeDoTipo, envelope.TipoComposto);

        var conteudo = Assert.IsType<TransacaoAvaliadaV1>(envelope.Conteudo);
        Assert.Equal("envelope", conteudo.IdentificadorExterno);
    }

    // -----------------------------------------------------------------------
    // Falhas obrigatorias (ROADMAP 5.11)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Falha_entre_o_commit_e_a_publicacao_deixa_o_evento_recuperavel()
    {
        // O processo caiu depois de decidir e antes de avisar. A avaliacao
        // existe, o evento esta na Outbox e ninguem foi avisado ainda.
        //
        // E exatamente por isso que a Outbox existe: sem ela, a decisao teria
        // sido gravada e a intencao de publicar teria morrido com o processo.
        using var resposta = await IngerirAsync("queda-antes-do-publish");
        resposta.EnsureSuccessStatusCode();

        Assert.Equal(1, await PendentesNaOutboxAsync());
        Assert.Equal(0, await _mensageria.Fila.ContarPendentesAsync(Fila, Cancelamento));

        // O processo volta. O despachante encontra o pendente e publica.
        await using var novoProcesso = CenarioDeMensageria.Criar(_banco.StringDeConexao);

        var despacho = await novoProcesso.Despachante.DespacharLoteAsync(Cancelamento);

        Assert.Equal(1, despacho.Publicados);
        Assert.Equal(0, await PendentesNaOutboxAsync());
    }

    [Fact]
    public async Task Publicacao_duplicada_produz_efeito_unico()
    {
        // O caso que o CLAUDE.md secao 40 chama de esperado: a mensagem foi
        // enviada, o processo caiu antes de marcar a Outbox, e o proximo ciclo
        // enviou de novo. Duas mensagens, o MESMO eventId.
        using var resposta = await IngerirAsync("publicacao-dupla");
        resposta.EnsureSuccessStatusCode();

        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        var original = Assert.Single(await _mensageria.Fila.ReceberAsync(Fila, 10, Cancelamento));
        await _mensageria.Fila.DevolverAsync(original.Recibo, Cancelamento);

        // A segunda publicacao do MESMO evento.
        await _mensageria.Fila.EnviarAsync(Fila, original.Corpo, Cancelamento);

        Assert.Equal(2, await _mensageria.Fila.ContarPendentesAsync(Fila, Cancelamento));

        var consumo = await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(1, consumo.Processados);
        Assert.Equal(1, consumo.Repetidos);

        // Uma linha na Inbox, um no contador. O contador e quem denuncia: se a
        // Inbox falhasse, ele estaria em 2 e nada mais mudaria.
        Assert.Equal(1, await ContagemNoResumoAsync(Decisao.Permitir));
        Assert.Equal(1, await LinhasNaInboxAsync());
    }

    [Fact]
    public async Task Worker_que_cai_depois_do_efeito_e_antes_do_ack_nao_duplica()
    {
        // A janela mais estreita e mais perigosa: o commit passou, a mensagem
        // ainda esta na fila. Ela volta, e o efeito NAO pode acontecer de novo.
        using var resposta = await IngerirAsync("queda-antes-do-ack");
        resposta.EnsureSuccessStatusCode();

        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        // Consome normalmente: efeito aplicado e mensagem apagada.
        Assert.Equal(1, (await _mensageria.Processador.ConsumirLoteAsync(Cancelamento)).Processados);
        Assert.Equal(1, await ContagemNoResumoAsync(Decisao.Permitir));

        // Simula a queda antes do ACK: a MESMA mensagem volta para a fila.
        var corpo = await CorpoDoEventoAsync();
        await _mensageria.Fila.EnviarAsync(Fila, corpo, Cancelamento);

        var reentrega = await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(0, reentrega.Processados);
        Assert.Equal(1, reentrega.Repetidos);
        Assert.Equal(1, await ContagemNoResumoAsync(Decisao.Permitir));
    }

    [Fact]
    public async Task Despachantes_concorrentes_publicam_cada_evento_uma_vez()
    {
        // `FOR UPDATE SKIP LOCKED` e o que permite mais de um despachante sem
        // coordenacao externa: cada um pega um conjunto diferente. Sem isso,
        // dois despachantes publicariam o mesmo evento duas vezes a cada
        // ciclo, e a duplicata deixaria de ser excecao para virar rotina.
        const int Eventos = 12;

        for (var i = 0; i < Eventos; i++)
        {
            using var resposta = await IngerirAsync($"concorrente-{i}");
            resposta.EnsureSuccessStatusCode();
        }

        await using var segundo = CenarioDeMensageria.Criar(_banco.StringDeConexao);
        await using var terceiro = CenarioDeMensageria.Criar(_banco.StringDeConexao);

        var resultados = await Task.WhenAll(
            _mensageria.Despachante.DespacharLoteAsync(Cancelamento),
            segundo.Despachante.DespacharLoteAsync(Cancelamento),
            terceiro.Despachante.DespacharLoteAsync(Cancelamento));

        Assert.Equal(Eventos, resultados.Sum(r => r.Publicados));
        Assert.Equal(0, await PendentesNaOutboxAsync());
        Assert.Equal(Eventos, await _mensageria.Fila.ContarPendentesAsync(Fila, Cancelamento));
    }

    [Fact]
    public async Task Mensagem_invalida_recorrente_termina_na_fila_de_mortas()
    {
        // O worker nao apaga o que nao entende: a DLQ e que guarda a mensagem
        // para analise. Devolver na hora so acelera o caminho ate la.
        await _mensageria.Fila.EnviarAsync(Fila, "{\"nao\":\"e um envelope\"}", Cancelamento);

        for (var i = 0; i < _mensageria.Opcoes.MaximoDeRecebimentos; i++)
        {
            var ciclo = await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

            Assert.Equal(1, ciclo.Recusados);
        }

        var ultimo = await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(0, ultimo.Total);
        Assert.Equal(0, await _mensageria.Fila.ContarPendentesAsync(Fila, Cancelamento));
        Assert.Equal(1, await _mensageria.Fila.ContarMortasAsync(Fila, Cancelamento));
    }

    // -----------------------------------------------------------------------
    // Falha parcial no lote (ROADMAP 5.8)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Mensagem_ruim_no_meio_do_lote_nao_impede_as_boas()
    {
        // Uma mensagem invalida no meio de tres boas. O lote NAO e reprocessado
        // inteiro: cada mensagem tem seu proprio destino.
        for (var i = 0; i < 3; i++)
        {
            using var resposta = await IngerirAsync($"boa-{i}");
            resposta.EnsureSuccessStatusCode();
        }

        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);
        await _mensageria.Fila.EnviarAsync(Fila, "isto nao e json", Cancelamento);

        var consumo = await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(3, consumo.Processados);
        Assert.Equal(1, consumo.Recusados);

        // As tres boas sairam da fila; so a ruim continua circulando.
        Assert.Equal(1, await _mensageria.Fila.ContarPendentesAsync(Fila, Cancelamento));
        Assert.Equal(3, await ContagemNoResumoAsync(Decisao.Permitir));
    }

    [Fact]
    public async Task Decisoes_diferentes_somam_em_linhas_diferentes()
    {
        // A projecao separa por decisao. Sem isso o painel diria "60
        // transacoes" e nao diria a unica coisa que interessa: quantas
        // precisam de gente olhando.
        var cliente = $"cli-decisoes-{Guid.NewGuid():N}"[..20];
        var inicio = DateTimeOffset.UtcNow.AddDays(-10);

        for (var i = 0; i < 3; i++)
        {
            using var normal = await IngerirAsync($"normal-{i}", cliente: cliente, ocorridaEm: inicio.AddDays(i));
            normal.EnsureSuccessStatusCode();
        }

        // Dispositivo novo (20) + pais novo (25) = 45 → Revisar.
        using var revisar = await IngerirAsync(
            "para-revisar",
            cliente: cliente,
            ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1),
            dispositivo: "disp-novo",
            pais: "PT");

        revisar.EnsureSuccessStatusCode();

        await _mensageria.RodarAteEsvaziarAsync(Cancelamento);

        Assert.Equal(3, await ContagemNoResumoAsync(Decisao.Permitir));
        Assert.Equal(1, await ContagemNoResumoAsync(Decisao.Revisar));
        Assert.Equal(0, await ContagemNoResumoAsync(Decisao.Bloquear));
    }

    // -----------------------------------------------------------------------
    // Auxiliares
    // -----------------------------------------------------------------------

    private Task<HttpResponseMessage> IngerirAsync(
        string identificador,
        string? correlacao = null,
        string? cliente = null,
        DateTimeOffset? ocorridaEm = null,
        string? dispositivo = "disp-abc",
        string? pais = "BR")
    {
        var requisicao = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(CenarioDeIngestao.CaminhoDaIngestao, UriKind.Relative))
        {
            Content = System.Net.Http.Json.JsonContent.Create(CenarioDeIngestao.Corpo(
                identificadorExterno: identificador,
                clienteExternoId: cliente ?? $"cli-{identificador}",
                ocorridaEm: ocorridaEm ?? DateTimeOffset.UtcNow.AddMinutes(-1),
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

    private async Task<int> PendentesNaOutboxAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        return await contexto.EventosDeSaida.CountAsync(e => e.PublicadoEm == null, Cancelamento);
    }

    private async Task<int> LinhasNaInboxAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        // So as marcas da projecao: o alerta e o outro consumidor, e contar os
        // dois juntos faria "quantas vezes este evento foi processado" dobrar
        // sem que nada tivesse acontecido duas vezes.
        return await contexto.EventosProcessados.CountAsync(
            e => e.Consumidor == ProjecaoDeDecisoesDiarias.NomeDoConsumidor,
            Cancelamento);
    }

    private async Task<int> ContagemNoResumoAsync(Decisao decisao)
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        return await contexto.ResumosDiarios
            .Where(r => r.Decisao == decisao)
            .SumAsync(r => r.Quantidade, Cancelamento);
    }

    /// <summary>Reconstroi o corpo da mensagem a partir do evento gravado.</summary>
    private async Task<string> CorpoDoEventoAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var evento = await contexto.EventosDeSaida.FirstAsync(Cancelamento);

        return SerializadorDeEnvelope.Serializar(EnvelopeDeEvento.De(evento));
    }
}
