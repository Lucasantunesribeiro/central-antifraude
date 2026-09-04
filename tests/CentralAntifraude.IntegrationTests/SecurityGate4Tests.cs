using System.Net;
using System.Text.Json;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Security Gate 4 (ROADMAP secao 4.9): o que muda quando as requisicoes
/// chegam juntas.
///
/// **A pergunta desta fase.** As protecoes das fases anteriores foram
/// verificadas uma requisicao por vez. Concorrencia e onde elas costumam
/// falhar de verdade: a validacao que passa duas vezes, o isolamento que vaza
/// porque duas transacoes leram ao mesmo tempo, o cliente que desiste no meio
/// e repete. Cada teste aqui existe porque a falha correspondente seria
/// invisivel em um cenario sequencial.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class SecurityGate4Tests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenantA = null!;
    private TenantDeTeste _tenantB = null!;
    private IntegracaoDeTeste _integracaoA = null!;
    private IntegracaoDeTeste _integracaoB = null!;

    public SecurityGate4Tests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        var sufixo = $"{Guid.NewGuid():N}"[..8];

        _tenantA = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"g4a-{sufixo}",
            Cancelamento);

        _tenantB = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"g4b-{sufixo}",
            Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();

        _integracaoA = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenantA, Cancelamento);
        _integracaoB = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenantB, Cancelamento);
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
    // Timeout do cliente seguido de retry
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cliente_que_desiste_no_meio_e_repete_nao_duplica_nada()
    {
        // O caso mais comum de retry no mundo real, e o mais perigoso: o
        // integrador estourou o proprio timeout e nao sabe se a transacao foi
        // registrada. Se o cancelamento tivesse deixado a operacao pela
        // metade, o retry criaria uma segunda transacao — ou pior, uma
        // transacao sem avaliacao.
        //
        // A transacao do banco resolve isso sozinha: um cancelamento derruba a
        // conexao antes do commit, e o PostgreSQL desfaz tudo.
        var corpo = CenarioDeIngestao.Corpo(
            identificadorExterno: "timeout-e-retry",
            clienteExternoId: $"cli-timeout-{Guid.NewGuid():N}"[..20]);

        var chave = $"idem-{Guid.NewGuid():N}";

        // Varias tentativas com prazos curtissimos e crescentes: alguma vai
        // cair no meio da operacao. Nao se afirma qual — o teste nao depende
        // de acertar o instante, so de nao aceitar dano.
        for (var i = 1; i <= 6; i++)
        {
            using var prazo = new CancellationTokenSource(TimeSpan.FromMilliseconds(i * 3));

            try
            {
                using var abortada = await CenarioDeIngestao.EnviarAsync(
                    _cliente,
                    _integracaoA.Chave,
                    chave,
                    corpo,
                    prazo.Token);
            }
            catch (OperationCanceledException)
            {
                // Exatamente o que o cliente com timeout observa.
            }
            catch (HttpRequestException)
            {
                // Conexao derrubada no meio. Tambem esperado.
            }
        }

        // Agora o retry "de verdade", sem pressa.
        using var retry = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracaoA.Chave,
            chave,
            corpo,
            Cancelamento);

        Assert.True(
            retry.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"retry devolveu {(int)retry.StatusCode}.");

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        // Uma transacao, uma avaliacao, um evento. Nem duplicata, nem
        // transacao orfa sem avaliacao.
        Assert.Equal(1, await contexto.Transacoes.CountAsync(Cancelamento));
        Assert.Equal(1, await contexto.AvaliacoesDeRisco.CountAsync(Cancelamento));
        Assert.Equal(1, await contexto.EventosDeSaida.CountAsync(Cancelamento));
    }

    [Fact]
    public async Task Requisicao_cancelada_nao_deixa_transacao_sem_avaliacao()
    {
        // A invariante que o boundary transacional garante: transacao e
        // avaliacao entram no mesmo commit. Se um cancelamento pudesse gravar
        // a primeira sem a segunda, existiria uma transacao que nenhuma tela
        // sabe mostrar e que nenhuma regra sabe corrigir depois.
        var alvo = $"cli-orfa-{Guid.NewGuid():N}"[..20];

        for (var i = 0; i < 12; i++)
        {
            using var prazo = new CancellationTokenSource(TimeSpan.FromMilliseconds(2 + i));

            try
            {
                using var abortada = await CenarioDeIngestao.EnviarAsync(
                    _cliente,
                    _integracaoA.Chave,
                    $"idem-{Guid.NewGuid():N}",
                    CenarioDeIngestao.Corpo(
                        identificadorExterno: $"orfa-{i}",
                        clienteExternoId: alvo),
                    prazo.Token);
            }
            catch (Exception excecao) when (excecao is OperationCanceledException or HttpRequestException)
            {
            }
        }

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        var transacoes = await contexto.Transacoes
            .Where(t => t.ClienteExternoId == alvo)
            .Select(t => t.Id)
            .ToListAsync(Cancelamento);

        var avaliadas = await contexto.AvaliacoesDeRisco
            .Where(a => transacoes.Contains(a.TransacaoId))
            .CountAsync(Cancelamento);

        Assert.Equal(transacoes.Count, avaliadas);
    }

    // -----------------------------------------------------------------------
    // Entrada maliciosa sob concorrencia
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Payload_malicioso_em_meio_a_rajada_e_recusado_sem_contaminar_o_resto()
    {
        // Doze requisicoes simultaneas: seis legitimas, seis carregando campo
        // interno que o contrato nao aceita.
        //
        // O risco especifico da concorrencia: uma validacao que dependa de
        // estado compartilhado pode "vazar" entre requisicoes sob carga. Se
        // isso acontecesse, uma requisicao maliciosa passaria porque a
        // validacao da vizinha rodou primeiro.
        var marca = $"{Guid.NewGuid():N}"[..8];

        var legitimas = Enumerable.Range(0, 6).Select(i => EnviarAsync(
            CenarioDeIngestao.Corpo(
                identificadorExterno: $"ok-{marca}-{i}",
                clienteExternoId: $"cli-ok-{marca}")));

        var maliciosas = Enumerable.Range(0, 6).Select(i => EnviarAsync(
            new Dictionary<string, object?>
            {
                ["identificadorExterno"] = $"mal-{marca}-{i}",
                ["valor"] = 100m,
                ["moeda"] = "BRL",
                ["ocorridaEm"] = DateTimeOffset.UtcNow.AddMinutes(-1),
                ["clienteExternoId"] = $"cli-mal-{marca}",
                ["referenciaDoInstrumento"] = "pi_demo_1",
                ["fingerprintDoDispositivo"] = "disp-1",
                ["enderecoIp"] = "203.0.113.10",
                ["paisDeOrigem"] = "BR",
                // Campo interno: o contrato fechado recusa.
                ["score"] = 0,
            }));

        var respostas = await Task.WhenAll(legitimas.Concat(maliciosas));

        var criadas = respostas.Count(r => r.StatusCode == HttpStatusCode.Created);
        var recusadas = respostas.Count(r => r.StatusCode == HttpStatusCode.BadRequest);

        Assert.Equal(6, criadas);
        Assert.Equal(6, recusadas);

        foreach (var resposta in respostas)
        {
            resposta.Dispose();
        }

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        // Nenhuma das maliciosas encostou no banco.
        Assert.Equal(
            0,
            await contexto.Transacoes
                .CountAsync(t => t.IdentificadorExterno.StartsWith("mal-"), Cancelamento));

        // E as legitimas nao foram prejudicadas pela vizinhanca.
        Assert.Equal(
            6,
            await contexto.Transacoes
                .CountAsync(t => t.IdentificadorExterno.StartsWith("ok-"), Cancelamento));
    }

    [Fact]
    public async Task Rajada_com_pan_valido_e_recusada_inteira()
    {
        // O detector de PAN da Fase 2, sob concorrencia. Um numero de cartao
        // que passe no Luhn nao pode entrar no banco por nenhuma porta —
        // inclusive a porta de "estava tudo muito rapido".
        var respostas = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => EnviarAsync(CenarioDeIngestao.Corpo(
                identificadorExterno: "4111111111111111",
                clienteExternoId: $"cli-pan-{i}"))));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));

        foreach (var resposta in respostas)
        {
            resposta.Dispose();
        }

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        Assert.Equal(0, await contexto.Transacoes.CountAsync(Cancelamento));
    }

    // -----------------------------------------------------------------------
    // Isolamento de tenant durante consultas historicas
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Dois_tenants_avaliando_o_mesmo_cliente_ao_mesmo_tempo_nao_se_misturam()
    {
        // O teste do Gate 3 provou o isolamento do contexto historico de forma
        // sequencial. Aqui os dois tenants avaliam AO MESMO TEMPO, com o mesmo
        // clienteExternoId — que e um identificador do integrador, e nao um
        // identificador global.
        //
        // Se o contexto vazasse, a rajada do tenant A faria as transacoes do
        // tenant B dispararem velocidade. O isolamento tem que valer inclusive
        // quando as duas leituras acontecem na mesma janela de milissegundos.
        const string ClienteCompartilhado = "cli-mesmo-id-nos-dois";
        var instante = DateTimeOffset.UtcNow.AddMinutes(-1);

        // A manda seis (passa do limite de 3); B manda duas (nao passa).
        var deA = Enumerable.Range(0, 6).Select(i => CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracaoA.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(
                identificadorExterno: $"a-{i}",
                clienteExternoId: ClienteCompartilhado,
                ocorridaEm: instante),
            Cancelamento));

        var deB = Enumerable.Range(0, 2).Select(i => CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracaoB.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(
                identificadorExterno: $"b-{i}",
                clienteExternoId: ClienteCompartilhado,
                ocorridaEm: instante),
            Cancelamento));

        var respostas = await Task.WhenAll(deA.Concat(deB));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        foreach (var resposta in respostas)
        {
            resposta.Dispose();
        }

        // Nenhuma transacao do tenant B disparou velocidade: para B, este
        // cliente tem duas transacoes, e nao oito.
        Assert.Equal(0, await SinaisDeVelocidadeAsync(_tenantB));

        // E em A, exatamente as tres que passaram do limite.
        Assert.Equal(3, await SinaisDeVelocidadeAsync(_tenantA));
    }

    [Fact]
    public async Task Cada_tenant_so_enxerga_os_proprios_eventos_de_saida()
    {
        // A Outbox guarda score, decisao e sinais. Um vazamento aqui entregaria
        // exatamente o que a avaliacao tem de mais sensivel — e a Fase 5 vai
        // ler esta tabela em lote, fora de uma requisicao, que e quando um
        // filtro esquecido costuma passar despercebido.
        using var deA = await EnviarAsync(CenarioDeIngestao.Corpo(
            identificadorExterno: "evento-de-a",
            clienteExternoId: "cli-a"));

        using var deB = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracaoB.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(
                identificadorExterno: "evento-de-b",
                clienteExternoId: "cli-b"),
            Cancelamento);

        deA.EnsureSuccessStatusCode();
        deB.EnsureSuccessStatusCode();

        await using var comoA = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        var eventoDeA = Assert.Single(await comoA.EventosDeSaida.ToListAsync(Cancelamento));
        Assert.Equal(_tenantA.OrganizacaoId, eventoDeA.OrganizacaoId);

        await using var comoB = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantB.OrganizacaoId);

        var eventoDeB = Assert.Single(await comoB.EventosDeSaida.ToListAsync(Cancelamento));
        Assert.Equal(_tenantB.OrganizacaoId, eventoDeB.OrganizacaoId);

        Assert.NotEqual(eventoDeA.Id, eventoDeB.Id);
    }

    [Fact]
    public async Task Consulta_historica_de_um_perfil_de_leitura_continua_isolada_sob_carga()
    {
        // O Auditor le, e so. Enquanto o tenant A ingere uma rajada, um Auditor
        // do tenant B nao pode ver nada disso — nem na listagem, nem no total.
        var marca = $"{Guid.NewGuid():N}"[..8];

        var ingestao = Task.WhenAll(
            Enumerable.Range(0, 10).Select(i => EnviarAsync(CenarioDeIngestao.Corpo(
                identificadorExterno: $"carga-{marca}-{i}",
                clienteExternoId: $"cli-carga-{marca}"))));

        var token = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenantB,
            PerfilDeUsuario.Auditor,
            Cancelamento);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Get,
            "/api/transacoes?tamanho=100",
            token);

        using var leitura = await _cliente.SendAsync(requisicao, Cancelamento);

        foreach (var resposta in await ingestao)
        {
            resposta.Dispose();
        }

        leitura.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(
            await leitura.Content.ReadAsStringAsync(Cancelamento));

        Assert.DoesNotContain(
            json.RootElement.GetProperty("itens").EnumerateArray(),
            item => item.GetProperty("identificadorExterno").GetString()!.Contains(marca, StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Auxiliares
    // -----------------------------------------------------------------------

    private Task<HttpResponseMessage> EnviarAsync(object corpo) =>
        CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracaoA.Chave,
            $"idem-{Guid.NewGuid():N}",
            corpo,
            Cancelamento);

    private async Task<int> SinaisDeVelocidadeAsync(TenantDeTeste tenant)
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            tenant.OrganizacaoId);

        return await contexto.AvaliacoesDeRisco
            .SelectMany(a => a.Sinais)
            .CountAsync(s => s.Tipo == TipoDeRegra.VelocidadePorCliente, Cancelamento);
    }
}
