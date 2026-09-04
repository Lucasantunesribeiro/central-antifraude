using System.Diagnostics;
using System.Net;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Baseline de performance do caminho critico, medida no ambiente de teste.
///
/// **Isto nao e um SLA.** O ROADMAP secao 4.8 pede explicitamente que nao se
/// invente numero de banco. O que existe aqui e uma referencia do ambiente
/// medido — Testcontainers, PostgreSQL 17 em Docker no Windows, tudo na mesma
/// maquina — para que uma regressao de ordem de grandeza apareca antes de
/// alguem descobri-la em producao.
///
/// Por isso os limites sao **generosos**. Um teste de tempo apertado em uma
/// maquina de desenvolvimento vira teste intermitente, e teste intermitente
/// acaba sendo desligado — o que e pior do que nao ter medida nenhuma.
///
/// Os numeros observados ficam registrados em `docs/baseline-de-performance.md`.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class BaselineDePerformanceTests : IAsyncLifetime
{
    /// <summary>Amostras da medicao sequencial.</summary>
    private const int Amostras = 40;

    /// <summary>
    /// Teto do p99 sequencial.
    ///
    /// Duas ordens de grandeza acima do observado. So dispara se algo
    /// estrutural quebrar — um N+1 novo, um indice perdido, uma consulta sem
    /// filtro.
    /// </summary>
    private static readonly TimeSpan TetoDoP99 = TimeSpan.FromSeconds(2);

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;

    public BaselineDePerformanceTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"perf-{Guid.NewGuid():N}"[..14],
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

    [Fact]
    public async Task Ingestao_sequencial_fica_dentro_da_ordem_de_grandeza_esperada()
    {
        var medidas = new List<double>(Amostras);

        // Duas requisicoes de aquecimento: a primeira paga JIT, abertura de
        // conexao e primeira compilacao de consulta do EF. Inclui-las na
        // amostra mediria a inicializacao, e nao o caminho critico.
        for (var i = 0; i < 2; i++)
        {
            using var aquecimento = await EnviarAsync($"aquece-{i}", $"cli-aquece-{i}");
            aquecimento.EnsureSuccessStatusCode();
        }

        for (var i = 0; i < Amostras; i++)
        {
            var relogio = Stopwatch.StartNew();

            using var resposta = await EnviarAsync($"perf-{i}", $"cli-perf-{i}");

            relogio.Stop();

            Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);

            medidas.Add(relogio.Elapsed.TotalMilliseconds);
        }

        var ordenadas = medidas.Order().ToList();

        var p50 = Percentil(ordenadas, 50);
        var p95 = Percentil(ordenadas, 95);
        var p99 = Percentil(ordenadas, 99);

        TestContext.Current.TestOutputHelper?.WriteLine(FormattableString.Invariant(
            $"ingestao sequencial ({Amostras} amostras): p50={p50:0.0}ms p95={p95:0.0}ms p99={p99:0.0}ms"));

        var diagnostico = FormattableString.Invariant(
            $"p99 de {p99:0.0}ms ultrapassou o teto de {TetoDoP99.TotalMilliseconds:0}ms. Isso costuma significar consulta nova sem indice, N+1 ou contencao inesperada.");

        Assert.True(p99 < TetoDoP99.TotalMilliseconds, diagnostico);
    }

    [Fact]
    public async Task Historico_longo_do_mesmo_cliente_nao_degrada_a_avaliacao()
    {
        // O risco real desta fase: a avaliacao le o passado do cliente. Se o
        // custo crescesse com o historico, um cliente antigo ficaria cada vez
        // mais lento — e o teto de 200 transacoes existe justamente para
        // impedir isso. Este teste verifica que o teto funciona.
        const string Cliente = "cli-historico-longo";

        // 20 dias, e nao 30: o limite de atraso da ingestao e de 30 dias
        // exatos, e comecar na borda faria a primeira transacao ser
        // recusada por milissegundos.
        var inicio = DateTimeOffset.UtcNow.AddDays(-20);

        for (var i = 0; i < 60; i++)
        {
            using var resposta = await EnviarAsync(
                $"historico-{i}",
                Cliente,
                inicio.AddMinutes(i * 30));

            resposta.EnsureSuccessStatusCode();
        }

        var relogio = Stopwatch.StartNew();

        using var comHistorico = await EnviarAsync(
            "historico-final",
            Cliente,
            DateTimeOffset.UtcNow.AddMinutes(-1));

        relogio.Stop();

        comHistorico.EnsureSuccessStatusCode();

        TestContext.Current.TestOutputHelper?.WriteLine(FormattableString.Invariant(
            $"ingestao com 60 transacoes no historico: {relogio.Elapsed.TotalMilliseconds:0.0}ms"));

        Assert.True(
            relogio.Elapsed < TetoDoP99,
            FormattableString.Invariant(
                $"avaliacao com historico levou {relogio.Elapsed.TotalMilliseconds:0.0}ms."));
    }

    [Fact]
    public async Task A_operacao_critica_usa_um_numero_previsivel_de_consultas()
    {
        // Contagem de consultas, e nao tempo: e a medida que nao depende da
        // maquina. Um N+1 introduzido por engano aparece aqui como um salto,
        // mesmo em um notebook lento.
        //
        // O caminho de uma transacao nova le: chave de idempotencia,
        // identificador externo, perfil vigente com as versoes de regra e o
        // historico do cliente. Depois grava transacao, avaliacao, sinais e
        // evento. O teto abaixo tem folga para variacao de plano, mas nao
        // para uma consulta por regra ou uma por sinal.
        const int TetoDeComandos = 25;

        using var aquecimento = await EnviarAsync("contagem-aquece", "cli-contagem");
        aquecimento.EnsureSuccessStatusCode();

        var antes = ContarComandos();

        using var medida = await EnviarAsync("contagem-medida", "cli-contagem-2");
        medida.EnsureSuccessStatusCode();

        var comandos = ContarComandos() - antes;

        TestContext.Current.TestOutputHelper?.WriteLine(
            FormattableString.Invariant($"comandos SQL na ingestao: {comandos}"));

        Assert.InRange(comandos, 1, TetoDeComandos);
    }

    // -----------------------------------------------------------------------
    // Auxiliares
    // -----------------------------------------------------------------------

    private Task<HttpResponseMessage> EnviarAsync(
        string identificador,
        string cliente,
        DateTimeOffset? ocorridaEm = null) =>
        CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracao.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(
                identificadorExterno: identificador,
                clienteExternoId: cliente,
                ocorridaEm: ocorridaEm ?? DateTimeOffset.UtcNow.AddMinutes(-1)),
            Cancelamento);

    /// <summary>
    /// Quantos comandos SQL o log do host registrou ate agora.
    ///
    /// O log de teste captura tudo em nivel Debug, e o EF Core registra cada
    /// comando executado. E uma forma de contar sem instalar interceptador
    /// so para isto.
    /// </summary>
    private int ContarComandos()
    {
        lock (_fabrica.Registros)
        {
            return _fabrica.Registros.Count(r =>
                r.Contains("Microsoft.EntityFrameworkCore.Database.Command", StringComparison.Ordinal));
        }
    }

    private static double Percentil(IReadOnlyList<double> ordenadas, int percentil)
    {
        var posicao = (int)Math.Ceiling(percentil / 100.0 * ordenadas.Count) - 1;

        return ordenadas[Math.Clamp(posicao, 0, ordenadas.Count - 1)];
    }
}
