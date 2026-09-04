using System.Net;
using System.Text.Json;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Uma avaliacao gravada nao muda mais — nem por replay, nem por regra nova,
/// nem por evento que chegou atrasado.
///
/// **Por que isto e uma invariante e nao uma preferencia.** A decisao de risco
/// e uma resposta que ja foi dada a um integrador, e sobre a qual alguem ja
/// agiu. Reescreve-la depois faz a trilha de auditoria mentir: o registro
/// passa a dizer que o sistema recomendou algo que ele nao recomendou na hora
/// em que importava (CLAUDE.md secoes 16, 17 e 24).
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class AvaliacaoCongeladaTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;

    public AvaliacaoCongeladaTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"congel-{Guid.NewGuid():N}"[..14],
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
    // Replay depois de nova versao de regra (ROADMAP 4.6 e 4.9)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Replay_depois_de_publicar_regra_nova_devolve_a_avaliacao_original()
    {
        // O cenario que o ROADMAP secao 4.6 chama de invariante critica.
        //
        // 1. o integrador envia uma transacao e recebe uma decisao;
        // 2. o perfil ganha uma versao nova, com peso maior;
        // 3. o integrador repete a MESMA requisicao (timeout, retry, o que
        //    for).
        //
        // Se o replay recalculasse, ele receberia uma decisao diferente para o
        // mesmo pedido — e nao teria como saber qual das duas vale. Pior: a
        // segunda contradiria a que ele ja usou para liberar o pagamento.
        var corpo = CenarioDeIngestao.Corpo(
            identificadorExterno: "replay-regra-nova",
            clienteExternoId: $"cli-replay-{Guid.NewGuid():N}"[..20],
            fingerprintDoDispositivo: "disp-1",
            paisDeOrigem: "BR");

        var chave = $"idem-{Guid.NewGuid():N}";

        using var primeira = await EnviarAsync(corpo, chave);
        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);

        using var jsonPrimeira = JsonDocument.Parse(
            await primeira.Content.ReadAsStringAsync(Cancelamento));

        var scoreOriginal = jsonPrimeira.RootElement.GetProperty("score").GetInt32();
        var decisaoOriginal = jsonPrimeira.RootElement.GetProperty("decisao").GetString();
        var avaliadaEmOriginal = jsonPrimeira.RootElement.GetProperty("avaliadaEm").GetDateTimeOffset();

        var versaoNova = await PublicarVersaoNovaDoPerfilAsync();

        using var replay = await EnviarAsync(corpo, chave);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using var jsonReplay = JsonDocument.Parse(
            await replay.Content.ReadAsStringAsync(Cancelamento));

        Assert.Equal(scoreOriginal, jsonReplay.RootElement.GetProperty("score").GetInt32());
        Assert.Equal(decisaoOriginal, jsonReplay.RootElement.GetProperty("decisao").GetString());
        Assert.Equal(avaliadaEmOriginal, jsonReplay.RootElement.GetProperty("avaliadaEm").GetDateTimeOffset());

        // E a avaliacao continua apontando para a versao ANTIGA do perfil.
        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(primeira);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var avaliacao = await contexto.AvaliacoesDeRisco
            .SingleAsync(a => a.TransacaoId == transacaoId, Cancelamento);

        Assert.NotEqual(versaoNova, avaliacao.VersaoDePerfilId);
        Assert.Equal(1, avaliacao.NumeroDaVersaoDePerfil);
    }

    [Fact]
    public async Task Transacao_nova_depois_da_regra_nova_usa_a_versao_nova()
    {
        // O outro lado da mesma invariante: congelar o passado nao pode
        // congelar o futuro. Uma transacao que chega DEPOIS da publicacao tem
        // que ser avaliada pela versao vigente, senao publicar regra nao
        // mudaria nada.
        var versaoNova = await PublicarVersaoNovaDoPerfilAsync();

        using var resposta = await EnviarAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: "depois-da-regra-nova",
            clienteExternoId: $"cli-depois-{Guid.NewGuid():N}"[..20]));

        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(resposta);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var avaliacao = await contexto.AvaliacoesDeRisco
            .SingleAsync(a => a.TransacaoId == transacaoId, Cancelamento);

        Assert.Equal(versaoNova, avaliacao.VersaoDePerfilId);
        Assert.Equal(2, avaliacao.NumeroDaVersaoDePerfil);
    }

    // -----------------------------------------------------------------------
    // Evento atrasado (ROADMAP 4.7)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Os_tres_tempos_sao_distintos_e_o_atraso_fica_visivel()
    {
        // OccurredAt, ReceivedAt e EvaluatedAt sao tres coisas diferentes
        // (CLAUDE.md secao 15). Uma transacao que aconteceu ha duas horas e
        // chegou agora nao pode parecer recem-ocorrida — e a diferenca entre
        // os dois primeiros e justamente o que denuncia o atraso.
        var ocorreuHaDuasHoras = DateTimeOffset.UtcNow.AddHours(-2);

        using var resposta = await EnviarAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: "atrasada-2h",
            clienteExternoId: $"cli-atraso-{Guid.NewGuid():N}"[..20],
            ocorridaEm: ocorreuHaDuasHoras));

        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(resposta);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var transacao = await contexto.Transacoes.SingleAsync(t => t.Id == transacaoId, Cancelamento);
        var avaliacao = await contexto.AvaliacoesDeRisco
            .SingleAsync(a => a.TransacaoId == transacaoId, Cancelamento);

        // O horario de origem e preservado, e nao substituido pelo do servidor.
        Assert.Equal(ocorreuHaDuasHoras.ToUniversalTime().Ticks / 10, transacao.OcorridaEm.Ticks / 10);

        // A chegada e o carimbo do servidor, sempre depois.
        Assert.True(transacao.RecebidaEm > transacao.OcorridaEm);
        Assert.True(transacao.RecebidaEm - transacao.OcorridaEm > TimeSpan.FromMinutes(110));

        // E a avaliacao aconteceu na chegada, nao na origem.
        Assert.True(avaliacao.AvaliadaEm >= transacao.RecebidaEm);
    }

    [Fact]
    public async Task Transacao_atrasada_nao_reescreve_a_avaliacao_de_uma_anterior()
    {
        // O caso do CLAUDE.md secao 16. Chegam, nesta ordem:
        //
        // 1. tres transacoes que ocorreram ha uma hora;
        // 2. uma quarta que OCORREU ha duas horas e chegou agora, atrasada.
        //
        // A atrasada e avaliada com o contexto dela — o passado que existia no
        // momento em que ela aconteceu, ou seja, nada. E as tres anteriores
        // continuam com o score que ja tinham: o sistema NAO volta atras para
        // recontar a janela delas agora que sabe da quarta.
        var cliente = $"cli-fora-de-ordem-{Guid.NewGuid():N}"[..24];
        var umaHoraAtras = DateTimeOffset.UtcNow.AddHours(-1);

        var idsIniciais = new List<Guid>();

        for (var i = 0; i < 3; i++)
        {
            using var resposta = await EnviarAsync(CenarioDeIngestao.Corpo(
                identificadorExterno: $"ordem-{i}",
                clienteExternoId: cliente,
                ocorridaEm: umaHoraAtras.AddSeconds(i)));

            idsIniciais.Add(await CenarioDeIngestao.IdDaTransacaoAsync(resposta));
        }

        Dictionary<Guid, (int Score, DateTimeOffset AvaliadaEm)> antes;

        await using (var leitura = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId))
        {
            antes = await leitura.AvaliacoesDeRisco
                .Where(a => idsIniciais.Contains(a.TransacaoId))
                .ToDictionaryAsync(a => a.TransacaoId, a => (a.Score, a.AvaliadaEm), Cancelamento);
        }

        using var atrasada = await EnviarAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: "ordem-atrasada",
            clienteExternoId: cliente,
            ocorridaEm: DateTimeOffset.UtcNow.AddHours(-2)));

        var idAtrasada = await CenarioDeIngestao.IdDaTransacaoAsync(atrasada);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var depois = await contexto.AvaliacoesDeRisco
            .Where(a => idsIniciais.Contains(a.TransacaoId))
            .ToDictionaryAsync(a => a.TransacaoId, a => (a.Score, a.AvaliadaEm), Cancelamento);

        Assert.Equal(antes, depois);

        // A atrasada enxergou o passado DELA: nada aconteceu antes das duas
        // horas atras, entao nenhum sinal de velocidade.
        var avaliacaoAtrasada = await contexto.AvaliacoesDeRisco
            .SingleAsync(a => a.TransacaoId == idAtrasada, Cancelamento);

        Assert.DoesNotContain(
            avaliacaoAtrasada.Sinais,
            s => s.Tipo == TipoDeRegra.VelocidadePorCliente);
    }

    // -----------------------------------------------------------------------
    // Outbox (ROADMAP 4.2)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cada_transacao_avaliada_grava_um_evento_pendente_com_a_decisao()
    {
        using var resposta = await EnviarAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: "com-evento",
            clienteExternoId: $"cli-evento-{Guid.NewGuid():N}"[..20]));

        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(resposta);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var evento = Assert.Single(await contexto.EventosDeSaida.ToListAsync(Cancelamento));

        Assert.Equal(TransacaoAvaliadaV1.NomeDoTipo, evento.Tipo);
        Assert.Equal(_tenant.OrganizacaoId, evento.OrganizacaoId);
        Assert.Null(evento.PublicadoEm);
        Assert.Equal(0, evento.TentativasDePublicacao);
        Assert.NotEmpty(evento.IdDeCorrelacao);

        // O conteudo volta como o record tipado, e nao como texto solto.
        var conteudo = Assert.IsType<TransacaoAvaliadaV1>(evento.Conteudo);

        Assert.Equal(transacaoId, conteudo.TransacaoId);
        Assert.Equal("com-evento", conteudo.IdentificadorExterno);
        Assert.Equal(Decisao.Permitir, conteudo.Decisao);
        Assert.Equal(0, conteudo.Score);
    }

    [Fact]
    public async Task Evento_nao_carrega_dado_sensivel()
    {
        // Minimizacao (CLAUDE.md secao 56): o evento leva o que um consumidor
        // precisa para agir. Referencia de instrumento, fingerprint de
        // dispositivo e fingerprint de IP ficam de fora — quem precisar deles
        // vai a transacao, dentro do tenant, com autorizacao.
        using var resposta = await EnviarAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: "evento-minimo",
            clienteExternoId: $"cli-min-{Guid.NewGuid():N}"[..20],
            referenciaDoInstrumento: "pi_segredo_do_instrumento",
            fingerprintDoDispositivo: "disp-secreto",
            enderecoIp: "203.0.113.77"));

        resposta.EnsureSuccessStatusCode();

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        // SQL cru NAO passa pelo filtro global de tenant - por isso a
        // organizacao entra explicitamente aqui. Sem ela, este teste leria os
        // eventos dos tenants dos outros testes da classe.
        var organizacao = _tenant.OrganizacaoId;

        var bruto = await contexto.Database
            .SqlQuery<string>(
                $"SELECT conteudo::text AS \"Value\" FROM eventos_de_saida WHERE organizacao_id = {organizacao}")
            .ToListAsync(Cancelamento);

        var json = Assert.Single(bruto);

        Assert.DoesNotContain("pi_segredo_do_instrumento", json, StringComparison.Ordinal);
        Assert.DoesNotContain("disp-secreto", json, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.77", json, StringComparison.Ordinal);
        Assert.DoesNotContain("fingerprint", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Replay_nao_gera_um_segundo_evento()
    {
        // O fato "esta transacao foi avaliada" aconteceu uma vez so. Publicar
        // de novo a cada retry do integrador criaria efeitos duplicados na
        // Fase 6 — um alerta por tentativa, e nao por transacao.
        var corpo = CenarioDeIngestao.Corpo(
            identificadorExterno: "evento-unico",
            clienteExternoId: $"cli-unico-{Guid.NewGuid():N}"[..20]);

        var chave = $"idem-{Guid.NewGuid():N}";

        for (var i = 0; i < 5; i++)
        {
            using var resposta = await EnviarAsync(corpo, chave);
            resposta.EnsureSuccessStatusCode();
        }

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        Assert.Equal(1, await contexto.EventosDeSaida.CountAsync(Cancelamento));
    }

    // -----------------------------------------------------------------------
    // Auxiliares
    // -----------------------------------------------------------------------

    private Task<HttpResponseMessage> EnviarAsync(object corpo, string? chaveDeIdempotencia = null) =>
        CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracao.Chave,
            chaveDeIdempotencia ?? $"idem-{Guid.NewGuid():N}",
            corpo,
            Cancelamento);

    /// <summary>
    /// Publica a versao 2 do perfil, com pesos maiores.
    ///
    /// Feito pelo dominio, e nao por SQL solto: e assim que a Fase 8 vai
    /// publicar de verdade, e um teste que montasse as linhas a mao poderia
    /// passar mesmo com o dominio quebrado.
    /// </summary>
    private async Task<Guid> PublicarVersaoNovaDoPerfilAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var agora = DateTimeOffset.UtcNow;

        var perfil = await contexto.PerfisDeRisco.SingleAsync(Cancelamento);
        var regras = await contexto.Regras.ToListAsync(Cancelamento);

        var versoesNovas = regras
            .Select(regra => VersaoDeRegra.Publicar(
                regra,
                numero: 2,
                ConfiguracaoNovaPara(regra.Tipo),
                // Peso dobrado: se o replay recalculasse, a diferenca seria
                // grande demais para passar despercebida.
                pontos: 90,
                agora))
            .ToList();

        var versaoDoPerfil = VersaoDePerfilDeRisco.Publicar(
            perfil,
            numero: 2,
            limiarDeRevisao: 10,
            limiarDeBloqueio: 20,
            versoesNovas,
            agora);

        contexto.VersoesDeRegra.AddRange(versoesNovas);
        contexto.VersoesDePerfilDeRisco.Add(versaoDoPerfil);

        await contexto.SaveChangesAsync(Cancelamento);

        return versaoDoPerfil.Id;
    }

    /// <summary>Configuracao que dispara facil, para a versao 2 ser visivel.</summary>
    private static ConfiguracaoDeRegra ConfiguracaoNovaPara(TipoDeRegra tipo) => tipo switch
    {
        TipoDeRegra.VelocidadePorCliente => new ConfiguracaoDeVelocidade(1, 1_440),
        TipoDeRegra.NovoDispositivo => new ConfiguracaoDeNovoDispositivo(1),
        TipoDeRegra.ValorAcimaDoHistorico => new ConfiguracaoDeValorAcimaDoHistorico(1.1m, 1),
        TipoDeRegra.DivergenciaGeografica => new ConfiguracaoDeDivergenciaGeografica(1),
        _ => throw new InvalidOperationException($"Tipo de regra sem configuracao de teste: {tipo}."),
    };
}
