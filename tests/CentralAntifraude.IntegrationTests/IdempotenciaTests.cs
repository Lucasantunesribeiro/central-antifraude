using System.Net;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Os quatro cenarios obrigatorios do ROADMAP secao 2.11, contra PostgreSQL
/// real.
///
/// O que importa aqui nao e o status HTTP: e quantas linhas existem no banco
/// no fim. Uma resposta 200 bonita com duas transacoes gravadas seria uma
/// falha silenciosa — e duplicidade num sistema antifraude significa avaliar
/// a mesma tentativa de pagamento duas vezes.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class IdempotenciaTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;

    public IdempotenciaTests(FixtureDoBanco banco) => _banco = banco;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"idem-{Guid.NewGuid():N}"[..14],
            TestContext.Current.CancellationToken);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();

        _integracao = await CenarioDeIngestao.CriarIntegracaoAsync(
            _cliente,
            _tenant,
            TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _cliente?.Dispose();
        if (_fabrica is not null)
        {
            await _fabrica.DisposeAsync();
        }
    }

    private Task<HttpResponseMessage> EnviarAsync(string chaveDeIdempotencia, object corpo) =>
        CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracao.Chave,
            chaveDeIdempotencia,
            corpo,
            TestContext.Current.CancellationToken);

    private async Task<int> ContarTransacoesAsync(string identificadorExterno)
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        return await contexto.Transacoes.CountAsync(
            t => t.IdentificadorExterno == identificadorExterno,
            TestContext.Current.CancellationToken);
    }

    // -----------------------------------------------------------------------
    // 2.11 — Sequencial
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Mesmo_pedido_enviado_dez_vezes_gera_uma_transacao()
    {
        var corpo = CenarioDeIngestao.Corpo(identificadorExterno: "seq-0001");
        var identificadores = new List<Guid>();

        for (var i = 0; i < 10; i++)
        {
            var resposta = await EnviarAsync("seq-chave-0001", corpo);

            Assert.Equal(
                i == 0 ? HttpStatusCode.Created : HttpStatusCode.OK,
                resposta.StatusCode);

            identificadores.Add(await CenarioDeIngestao.IdDaTransacaoAsync(resposta));
        }

        // Todas as respostas apontam para a MESMA transacao.
        Assert.Single(identificadores.Distinct());
        Assert.Equal(1, await ContarTransacoesAsync("seq-0001"));
    }

    [Fact]
    public async Task A_primeira_resposta_diz_registrada_e_as_seguintes_dizem_ja_registrada()
    {
        // O integrador precisa dessa diferenca para saber se o retry dele foi
        // necessario ou se a primeira tentativa ja tinha chegado.
        var corpo = CenarioDeIngestao.Corpo(identificadorExterno: "seq-0002");

        var primeira = await EnviarAsync("seq-chave-0002", corpo);
        var segunda = await EnviarAsync("seq-chave-0002", corpo);

        Assert.Equal("registrada", await CenarioDeIngestao.SituacaoAsync(primeira));
        Assert.Equal("ja_registrada", await CenarioDeIngestao.SituacaoAsync(segunda));
    }

    // -----------------------------------------------------------------------
    // 2.11 — Concorrente
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Vinte_requisicoes_simultaneas_geram_uma_unica_transacao()
    {
        // Este e o teste que a consulta-antes-de-inserir sozinha NAO passa:
        // as vinte requisicoes consultam ao mesmo tempo, todas acham nada, e
        // todas tentam inserir. Quem segura e a restricao unica do banco.
        var corpo = CenarioDeIngestao.Corpo(identificadorExterno: "corrida-0001");

        var respostas = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => EnviarAsync("corrida-chave-0001", corpo)));

        Assert.All(respostas, r => Assert.True(
            r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Status inesperado sob concorrencia: {(int)r.StatusCode}."));

        // Exatamente uma criou; as outras dezenove reconheceram a existente.
        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));

        var identificadores = new List<Guid>();
        foreach (var resposta in respostas)
        {
            identificadores.Add(await CenarioDeIngestao.IdDaTransacaoAsync(resposta));
        }

        Assert.Single(identificadores.Distinct());
        Assert.Equal(1, await ContarTransacoesAsync("corrida-0001"));

        foreach (var resposta in respostas)
        {
            resposta.Dispose();
        }
    }

    [Fact]
    public async Task Requisicoes_simultaneas_com_o_mesmo_identificador_externo_e_chaves_diferentes_nao_duplicam()
    {
        // A segunda barreira sob concorrencia: chaves de idempotencia
        // diferentes, mas a mesma tentativa de pagamento. A restricao de
        // negocio (organizacao + integracao + identificadorExterno) e quem
        // impede a duplicata.
        var corpo = CenarioDeIngestao.Corpo(identificadorExterno: "corrida-0002");

        var respostas = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(i => EnviarAsync($"corrida-chave-002-{i:D2}", corpo)));

        Assert.All(respostas, r => Assert.True(
            r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Status inesperado: {(int)r.StatusCode}."));

        Assert.Equal(1, await ContarTransacoesAsync("corrida-0002"));

        foreach (var resposta in respostas)
        {
            resposta.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // 2.11 — Payload conflitante
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Mesma_chave_com_conteudo_diferente_e_conflito()
    {
        await EnviarAsync("conf-chave-0001", CenarioDeIngestao.Corpo(identificadorExterno: "conf-0001"));

        var conflito = await EnviarAsync(
            "conf-chave-0001",
            CenarioDeIngestao.Corpo(identificadorExterno: "conf-0001", valor: 999.00m));

        Assert.Equal(HttpStatusCode.Conflict, conflito.StatusCode);
        Assert.Equal("idempotencia_conflitante", await CenarioDeIngestao.CodigoDoErroAsync(conflito));

        // E, o mais importante: nada foi sobrescrito.
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var transacao = await contexto.Transacoes.FirstAsync(
            t => t.IdentificadorExterno == "conf-0001",
            TestContext.Current.CancellationToken);

        Assert.Equal(249.90m, transacao.Valor.Valor);
    }

    [Fact]
    public async Task Diferenca_apenas_de_formatacao_nao_e_conflito()
    {
        // "249.90" e "249.9000" sao o mesmo dinheiro. Tratar isso como
        // conflito quebraria um retry legitimo de um cliente que serializa
        // decimal de outro jeito.
        var ocorridaEm = DateTimeOffset.UtcNow.AddMinutes(-2);

        var primeira = await EnviarAsync(
            "fmt-chave-0001",
            CenarioDeIngestao.Corpo(identificadorExterno: "fmt-0001", valor: 249.90m, ocorridaEm: ocorridaEm));

        var segunda = await EnviarAsync(
            "fmt-chave-0001",
            CenarioDeIngestao.Corpo(
                identificadorExterno: "fmt-0001",
                valor: 249.9000m,
                ocorridaEm: ocorridaEm.ToOffset(TimeSpan.FromHours(-3))));

        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);
    }

    // -----------------------------------------------------------------------
    // 2.11 — External ID conflitante
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Chave_nova_com_o_mesmo_identificador_externo_equivalente_devolve_a_original()
    {
        var corpo = CenarioDeIngestao.Corpo(identificadorExterno: "ext-0001");

        var primeira = await EnviarAsync("ext-chave-0001", corpo);
        var segunda = await EnviarAsync("ext-chave-0002", corpo);

        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);

        Assert.Equal(
            await CenarioDeIngestao.IdDaTransacaoAsync(primeira),
            await CenarioDeIngestao.IdDaTransacaoAsync(segunda));

        Assert.Equal(1, await ContarTransacoesAsync("ext-0001"));
    }

    [Fact]
    public async Task Chave_nova_com_o_mesmo_identificador_externo_conflitante_e_conflito()
    {
        await EnviarAsync("ext-chave-0003", CenarioDeIngestao.Corpo(identificadorExterno: "ext-0002"));

        var conflito = await EnviarAsync(
            "ext-chave-0004",
            CenarioDeIngestao.Corpo(identificadorExterno: "ext-0002", valor: 1.00m));

        Assert.Equal(HttpStatusCode.Conflict, conflito.StatusCode);
        Assert.Equal("transacao_externa_conflitante", await CenarioDeIngestao.CodigoDoErroAsync(conflito));
    }

    // -----------------------------------------------------------------------
    // Isolamento
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_mesma_chave_em_integracoes_diferentes_nao_colide()
    {
        // A chave de idempotencia e escopada por organizacao + integracao
        // (CLAUDE.md secao 33). Dois integradores do mesmo cliente podem usar
        // "pedido-1" sem que um sobrescreva o outro.
        var outra = await CenarioDeIngestao.CriarIntegracaoAsync(
            _cliente,
            _tenant,
            TestContext.Current.CancellationToken,
            nome: "Segunda integracao");

        var corpo = CenarioDeIngestao.Corpo(identificadorExterno: "iso-0001");

        var naPrimeira = await EnviarAsync("iso-chave-0001", corpo);
        var naSegunda = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            outra.Chave,
            "iso-chave-0001",
            corpo,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, naPrimeira.StatusCode);
        Assert.Equal(HttpStatusCode.Created, naSegunda.StatusCode);
        Assert.Equal(2, await ContarTransacoesAsync("iso-0001"));
    }
}
