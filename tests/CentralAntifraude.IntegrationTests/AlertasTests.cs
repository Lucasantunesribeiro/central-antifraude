using System.Globalization;
using System.Text.Json;
using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Infrastructure.Mensageria;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// A fila operacional, do evento ate a tela.
///
/// **O alerta e o primeiro efeito do backbone que uma pessoa ve.** A projecao
/// diaria da Fase 5 era um contador; este e trabalho na mesa de alguem. Isso
/// muda o que precisa ser provado: um contador errado e um numero errado, um
/// alerta duplicado e um analista investigando duas vezes o mesmo caso.
///
/// Por isso a idempotencia aqui tem **duas camadas**, e os dois testes de
/// duplicidade abaixo derrubam uma de cada vez para mostrar que a outra
/// sozinha ja segura.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class AlertasTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;
    private CenarioDeMensageria _mensageria = null!;

    public AlertasTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"alerta-{Guid.NewGuid():N}"[..14],
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
    // A politica
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Permitir_nao_gera_alerta()
    {
        // A maioria absoluta das transacoes cai aqui. Se `Permitir` gerasse
        // alerta, a fila seria um espelho da tabela de transacoes — que ja
        // existe, na tela de Transacoes.
        var decisao = await IngerirEProcessarAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: "tranquila",
            clienteExternoId: $"cli-{Guid.NewGuid():N}"[..16]));

        Assert.Equal(nameof(Decisao.Permitir), decisao);
        Assert.Empty(await AlertasNoBancoAsync());
    }

    [Fact]
    public async Task Revisar_gera_alerta_de_prioridade_media()
    {
        var (decisao, transacaoId) = await IngerirCenarioDeRevisaoAsync("revisar-1");

        Assert.Equal(nameof(Decisao.Revisar), decisao);

        var alerta = Assert.Single(await AlertasNoBancoAsync());

        Assert.Equal(PrioridadeDeAlerta.Media, alerta.Prioridade);
        Assert.Equal(Decisao.Revisar, alerta.Decisao);
        Assert.Equal(45, alerta.Score);
        Assert.Equal(transacaoId, alerta.TransacaoId);
        Assert.Equal(StatusDoAlerta.Aberto, alerta.Status);
        Assert.Equal(PoliticaDeAlertas.Versao, alerta.VersaoDaPolitica);
    }

    [Fact]
    public async Task Bloquear_gera_alerta_de_prioridade_alta()
    {
        var (decisao, _) = await IngerirCenarioDeBloqueioAsync("bloquear-1");

        Assert.Equal(nameof(Decisao.Bloquear), decisao);

        var alerta = Assert.Single(await AlertasNoBancoAsync());

        Assert.Equal(PrioridadeDeAlerta.Alta, alerta.Prioridade);
        Assert.Equal(75, alerta.Score);
    }

    [Fact]
    public async Task O_alerta_liga_a_avaliacao_ao_evento_e_a_requisicao()
    {
        // O ultimo elo da corrente do CLAUDE.md secao 69, agora gravado no
        // alerta: a pergunta "qual requisicao gerou este trabalho?" continua
        // com resposta depois que o log sumir com a retencao.
        var (_, transacaoId) = await IngerirCenarioDeRevisaoAsync("procedencia");

        var alerta = Assert.Single(await AlertasNoBancoAsync());

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var avaliacao = await contexto.AvaliacoesDeRisco
            .SingleAsync(a => a.TransacaoId == transacaoId, Cancelamento);

        Assert.Equal(avaliacao.Id, alerta.AvaliacaoId);
        Assert.Equal(avaliacao.AvaliadaEm, alerta.AvaliadaEm);

        // O evento citado existe mesmo na Outbox, e carrega a mesma correlacao.
        var evento = await contexto.EventosDeSaida.SingleAsync(
            e => e.Id == alerta.EventoId,
            Cancelamento);

        Assert.Equal(evento.IdDeCorrelacao, alerta.IdDeCorrelacao);
        Assert.NotEmpty(alerta.IdDeCorrelacao);

        // O alerta apareceu DEPOIS da decisao: o caminho e assincrono, e a
        // distancia entre os dois e a latencia real do backbone.
        Assert.True(alerta.CriadoEm >= alerta.AvaliadaEm);
    }

    // -----------------------------------------------------------------------
    // Idempotencia, uma camada de cada vez
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Evento_entregue_de_novo_nao_duplica_o_alerta()
    {
        await IngerirCenarioDeRevisaoAsync("reentrega");

        var alerta = Assert.Single(await AlertasNoBancoAsync());

        // Republica o MESMO evento: e exatamente o que acontece quando o
        // processo cai entre publicar e marcar a Outbox. So ele, e nao os
        // eventos das transacoes de historico — assim a contagem do consumo
        // diz respeito ao que o teste afirma.
        await RepublicarOutboxAsync(alerta.EventoId);
        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        var consumo = await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(0, consumo.Processados);
        Assert.Equal(1, consumo.Repetidos);
        Assert.Single(await AlertasNoBancoAsync());
    }

    [Fact]
    public async Task Sem_a_marca_da_inbox_a_restricao_do_banco_ainda_segura_o_alerta()
    {
        // **A segunda camada, sozinha.** O CLAUDE.md secao 42 pede que a
        // idempotencia exista em mais de uma camada quando o dominio
        // justificar — e aqui justifica: alerta duplicado nao e um numero
        // errado num painel, e um analista investigando duas vezes o mesmo
        // caso.
        //
        // Este teste apaga a marca do criador de alertas na Inbox e reentrega
        // o evento. O worker acredita que nunca tratou aquilo, tenta criar o
        // alerta, e quem recusa e a restricao unica de `avaliacao_id`.
        await IngerirCenarioDeRevisaoAsync("segunda-camada");

        var primeiro = Assert.Single(await AlertasNoBancoAsync());

        await ApagarMarcaDaInboxAsync(CriadorDeAlertas.NomeDoConsumidor);
        await RepublicarOutboxAsync(primeiro.EventoId);
        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        var depois = Assert.Single(await AlertasNoBancoAsync());

        // Mesmo alerta, e nao um segundo: o identificador nao mudou.
        Assert.Equal(primeiro.Id, depois.Id);
        Assert.Equal(primeiro.CriadoEm, depois.CriadoEm);
    }

    [Fact]
    public async Task Os_dois_efeitos_marcam_a_inbox_separadamente()
    {
        // A chave da Inbox e (consumidor, evento), e nao so o evento. Marcar
        // apenas pelo evento faria o segundo manipulador achar que o trabalho
        // dele ja tinha sido feito — e a fila nunca receberia alerta nenhum.
        await IngerirCenarioDeRevisaoAsync("dois-efeitos");

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var alerta = Assert.Single(await contexto.Alertas.ToListAsync(Cancelamento));

        var consumidores = await contexto.EventosProcessados
            .Where(e => e.EventoId == alerta.EventoId)
            .Select(e => e.Consumidor)
            .ToListAsync(Cancelamento);

        Assert.Equal(
            [CriadorDeAlertas.NomeDoConsumidor, ProjecaoDeDecisoesDiarias.NomeDoConsumidor],
            consumidores.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Um_efeito_ja_aplicado_nao_impede_o_outro()
    {
        // O savepoint por manipulador existe para isto. No PostgreSQL um erro
        // aborta a transacao inteira: sem savepoint, o conflito da marca ja
        // gravada mataria tambem o efeito que ainda nao tinha acontecido.
        //
        // Aqui a marca do criador de alertas e apagada e a da projecao fica.
        // A projecao vai conflitar; o alerta precisa sobreviver.
        var (_, transacaoId) = await IngerirCenarioDeRevisaoAsync("savepoint");

        var alertaOriginal = Assert.Single(await AlertasNoBancoAsync());

        Assert.Equal(1, await ContagemNoResumoAsync(Decisao.Revisar));

        await ApagarAlertaAsync();
        await ApagarMarcaDaInboxAsync(CriadorDeAlertas.NomeDoConsumidor);
        await RepublicarOutboxAsync(alertaOriginal.EventoId);
        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        await _mensageria.Processador.ConsumirLoteAsync(Cancelamento);

        // O alerta voltou: o conflito da projecao nao contaminou o savepoint
        // do criador de alertas.
        var alerta = Assert.Single(await AlertasNoBancoAsync());
        Assert.Equal(transacaoId, alerta.TransacaoId);

        // E o contador NAO somou de novo: a marca da projecao continuava la.
        Assert.Equal(1, await ContagemNoResumoAsync(Decisao.Revisar));
    }

    // -----------------------------------------------------------------------
    // A fila, pela API
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_fila_devolve_o_alerta_com_os_principais_sinais()
    {
        var (_, transacaoId) = await IngerirCenarioDeRevisaoAsync("na-fila");

        using var json = await ConsultarFilaAsync();
        var itens = json.RootElement.GetProperty("itens");

        Assert.Equal(1, json.RootElement.GetProperty("total").GetInt64());

        var alerta = Assert.Single(itens.EnumerateArray());

        Assert.Equal(nameof(Decisao.Revisar), alerta.GetProperty("decisao").GetString());
        Assert.Equal(nameof(PrioridadeDeAlerta.Media), alerta.GetProperty("prioridade").GetString());
        Assert.Equal(45, alerta.GetProperty("score").GetInt32());

        // A navegacao do ROADMAP 6.6: da fila para a transacao, e de la para a
        // avaliacao e os sinais completos, que a tela da Fase 3 ja mostra.
        Assert.Equal(transacaoId, alerta.GetProperty("transacaoId").GetGuid());

        var sinais = alerta.GetProperty("principaisSinais")
            .EnumerateArray()
            .Select(s => s.GetProperty("tipo").GetString())
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [nameof(TipoDeRegra.DivergenciaGeografica), nameof(TipoDeRegra.NovoDispositivo)],
            sinais);

        Assert.Equal(2, alerta.GetProperty("totalDeSinais").GetInt32());
    }

    [Fact]
    public async Task A_fila_filtra_por_prioridade_decisao_e_score()
    {
        await IngerirCenarioDeRevisaoAsync("filtro-media");
        await IngerirCenarioDeBloqueioAsync("filtro-alta");

        Assert.Equal(2, await TotalNaFilaAsync(string.Empty));

        Assert.Equal(1, await TotalNaFilaAsync("prioridade=Alta"));
        Assert.Equal(1, await TotalNaFilaAsync("prioridade=Media"));
        Assert.Equal(1, await TotalNaFilaAsync("decisao=Bloquear"));
        Assert.Equal(2, await TotalNaFilaAsync("scoreMinimo=45"));
        Assert.Equal(1, await TotalNaFilaAsync("scoreMinimo=70"));
        Assert.Equal(0, await TotalNaFilaAsync("scoreMinimo=100"));

        // Combinados: os filtros se somam, e nao se substituem.
        Assert.Equal(1, await TotalNaFilaAsync("decisao=Bloquear&scoreMinimo=70"));
        Assert.Equal(0, await TotalNaFilaAsync("decisao=Revisar&scoreMinimo=70"));
    }

    [Fact]
    public async Task A_fila_filtra_por_periodo_de_criacao()
    {
        await IngerirCenarioDeRevisaoAsync("periodo");

        var agora = DateTimeOffset.UtcNow;
        var ontem = Uri.EscapeDataString(agora.AddDays(-1).ToString("O", CultureInfo.InvariantCulture));
        var amanha = Uri.EscapeDataString(agora.AddDays(1).ToString("O", CultureInfo.InvariantCulture));

        Assert.Equal(1, await TotalNaFilaAsync($"de={ontem}&ate={amanha}"));
        Assert.Equal(0, await TotalNaFilaAsync($"de={amanha}"));
        Assert.Equal(0, await TotalNaFilaAsync($"ate={ontem}"));
    }

    [Fact]
    public async Task A_fila_ordena_por_score_e_por_prioridade()
    {
        await IngerirCenarioDeRevisaoAsync("ordem-media");
        await IngerirCenarioDeBloqueioAsync("ordem-alta");

        Assert.Equal([75, 45], await ScoresNaFilaAsync("ordenarPor=score&direcao=desc"));
        Assert.Equal([45, 75], await ScoresNaFilaAsync("ordenarPor=score&direcao=asc"));

        // Prioridade e um enum ordenado: Alta pesa mais que Media, e a
        // ordenacao respeita isso em vez de ordenar pelo texto.
        Assert.Equal([75, 45], await ScoresNaFilaAsync("ordenarPor=prioridade&direcao=desc"));
        Assert.Equal([45, 75], await ScoresNaFilaAsync("ordenarPor=prioridade&direcao=asc"));
    }

    [Fact]
    public async Task A_fila_pagina_no_servidor()
    {
        await IngerirCenarioDeRevisaoAsync("pag-1");
        await IngerirCenarioDeBloqueioAsync("pag-2");

        using var primeira = await ConsultarFilaAsync("tamanho=1&pagina=1&ordenarPor=score&direcao=desc");
        using var segunda = await ConsultarFilaAsync("tamanho=1&pagina=2&ordenarPor=score&direcao=desc");

        // O total e o total de verdade, e nao o tamanho da pagina: uma tela
        // que dissesse "1 alerta" com dois na fila mentiria sobre o tamanho do
        // trabalho.
        Assert.Equal(2, primeira.RootElement.GetProperty("total").GetInt64());
        Assert.Equal(2, primeira.RootElement.GetProperty("totalDePaginas").GetInt32());

        Assert.Equal(75, Assert.Single(primeira.RootElement.GetProperty("itens").EnumerateArray())
            .GetProperty("score").GetInt32());
        Assert.Equal(45, Assert.Single(segunda.RootElement.GetProperty("itens").EnumerateArray())
            .GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task A_fila_vazia_e_uma_lista_vazia_e_nao_um_erro()
    {
        using var json = await ConsultarFilaAsync();

        Assert.Empty(json.RootElement.GetProperty("itens").EnumerateArray());
        Assert.Equal(0, json.RootElement.GetProperty("total").GetInt64());
    }

    // -----------------------------------------------------------------------
    // Cenarios de ingestao
    // -----------------------------------------------------------------------

    /// <summary>
    /// Dispositivo novo (20) + pais novo (25) = 45, acima do limiar de revisao.
    ///
    /// O historico e espacado em dias de proposito: se fosse em minutos, a
    /// regra de velocidade tambem acionaria e o score deixaria de ser o que o
    /// teste afirma.
    /// </summary>
    private async Task<(string Decisao, Guid TransacaoId)> IngerirCenarioDeRevisaoAsync(string prefixo)
    {
        var cliente = $"cli-{prefixo}-{Guid.NewGuid():N}"[..24];

        await CriarHistoricoAsync(prefixo, cliente, valor: 100m);

        var (decisao, id) = await IngerirComRetornoAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: $"{prefixo}-alvo",
            valor: 100m,
            clienteExternoId: cliente,
            ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1),
            fingerprintDoDispositivo: "disp-novo",
            paisDeOrigem: "PT"));

        await _mensageria.RodarAteEsvaziarAsync(Cancelamento);

        return (decisao, id);
    }

    /// <summary>
    /// Valor acima do historico (30) + dispositivo novo (20) + pais novo (25)
    /// = 75, acima do limiar de bloqueio.
    /// </summary>
    private async Task<(string Decisao, Guid TransacaoId)> IngerirCenarioDeBloqueioAsync(string prefixo)
    {
        var cliente = $"cli-{prefixo}-{Guid.NewGuid():N}"[..24];

        await CriarHistoricoAsync(prefixo, cliente, valor: 100m);

        var (decisao, id) = await IngerirComRetornoAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: $"{prefixo}-alvo",
            valor: 900m,
            clienteExternoId: cliente,
            ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1),
            fingerprintDoDispositivo: "disp-novo",
            paisDeOrigem: "PT"));

        await _mensageria.RodarAteEsvaziarAsync(Cancelamento);

        return (decisao, id);
    }

    private async Task CriarHistoricoAsync(string prefixo, string cliente, decimal valor)
    {
        var inicio = DateTimeOffset.UtcNow.AddDays(-10);

        for (var i = 0; i < 3; i++)
        {
            await IngerirComRetornoAsync(CenarioDeIngestao.Corpo(
                identificadorExterno: $"{prefixo}-hist-{i}",
                valor: valor,
                clienteExternoId: cliente,
                ocorridaEm: inicio.AddDays(i),
                fingerprintDoDispositivo: "disp-de-casa",
                paisDeOrigem: "BR"));
        }
    }

    private async Task<string> IngerirEProcessarAsync(object corpo)
    {
        var (decisao, _) = await IngerirComRetornoAsync(corpo);

        await _mensageria.RodarAteEsvaziarAsync(Cancelamento);

        return decisao;
    }

    private async Task<(string Decisao, Guid TransacaoId)> IngerirComRetornoAsync(object corpo)
    {
        using var resposta = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracao.Chave,
            $"idem-{Guid.NewGuid():N}",
            corpo,
            Cancelamento);

        resposta.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(
            await resposta.Content.ReadAsStringAsync(Cancelamento));

        return (
            json.RootElement.GetProperty("decisao").GetString()!,
            json.RootElement.GetProperty("id").GetGuid());
    }

    // -----------------------------------------------------------------------
    // Leitura
    // -----------------------------------------------------------------------

    private async Task<IReadOnlyList<Alerta>> AlertasNoBancoAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        return await contexto.Alertas.OrderBy(a => a.CriadoEm).ToListAsync(Cancelamento);
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

    private async Task<JsonDocument> ConsultarFilaAsync(string consulta = "")
    {
        var token = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenant,
            PerfilDeUsuario.AnalistaDeFraude,
            Cancelamento);

        var caminho = string.IsNullOrEmpty(consulta) ? "/api/alertas" : $"/api/alertas?{consulta}";

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, caminho, token);
        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        resposta.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(Cancelamento));
    }

    private async Task<long> TotalNaFilaAsync(string consulta)
    {
        using var json = await ConsultarFilaAsync(consulta);

        return json.RootElement.GetProperty("total").GetInt64();
    }

    private async Task<IReadOnlyList<int>> ScoresNaFilaAsync(string consulta)
    {
        using var json = await ConsultarFilaAsync(consulta);

        return [.. json.RootElement.GetProperty("itens")
            .EnumerateArray()
            .Select(a => a.GetProperty("score").GetInt32())];
    }

    // -----------------------------------------------------------------------
    // Manipulacao deliberada do estado, para derrubar uma camada por vez
    // -----------------------------------------------------------------------

    private async Task RepublicarOutboxAsync(Guid eventoId)
    {
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        await contexto.Database.ExecuteSqlRawAsync(
            "UPDATE eventos_de_saida SET publicado_em = NULL WHERE id = {0}",
            [eventoId],
            Cancelamento);
    }

    private async Task ApagarMarcaDaInboxAsync(string consumidor)
    {
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        await contexto.Database.ExecuteSqlRawAsync(
            "DELETE FROM eventos_processados WHERE organizacao_id = {0} AND consumidor = {1}",
            [_tenant.OrganizacaoId, consumidor],
            Cancelamento);
    }

    private async Task ApagarAlertaAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        await contexto.Database.ExecuteSqlRawAsync(
            "DELETE FROM alertas WHERE organizacao_id = {0}",
            [_tenant.OrganizacaoId],
            Cancelamento);
    }
}
