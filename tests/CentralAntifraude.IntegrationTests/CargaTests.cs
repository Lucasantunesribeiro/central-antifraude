using System.Diagnostics;
using System.Globalization;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Os quatro cenarios de carga do ROADMAP 12.7 — medidos, e nao afirmados.
///
/// **A diferenca entre esta classe e `ConcorrenciaTests`.** Aquela pergunta se
/// o resultado esta CORRETO sob concorrencia; esta pergunta quanto custa. Sao
/// perguntas diferentes e as duas precisam de resposta: um sistema pode estar
/// perfeitamente correto e inutilmente lento, e o contrario tambem.
///
/// **Os tetos sao generosos de proposito.** O ROADMAP 12.7 e explicito: medir,
/// nao inventar SLA. Um limite apertado numa maquina de desenvolvimento vira
/// teste intermitente, e teste intermitente acaba desligado — o que e pior do
/// que nao medir. O que estes limites pegam e regressao de ORDEM DE GRANDEZA:
/// alguem que introduza um N+1 na avaliacao ou um lock desnecessario descobre
/// aqui, e nao em producao.
///
/// Os numeros observados ficam em `docs/baseline-de-performance.md`, sempre com
/// o ambiente ao lado — numero de latencia sem ambiente e enfeite.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class CargaTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;

    public CargaTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"carga-{Guid.NewGuid():N}"[..14],
            Cancelamento);

        await CenarioDeMensageria.LimparAsync(_banco.StringDeConexao, Cancelamento);

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
    // Cenario A — throughput normal
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cenario_A_clientes_independentes_em_paralelo()
    {
        const int Clientes = 8;
        const int PorCliente = 8;
        const int Total = Clientes * PorCliente;

        var relogio = Stopwatch.StartNew();

        var lotes = Enumerable.Range(0, Clientes).Select(async cliente =>
        {
            var resultados = new List<(System.Net.HttpStatusCode Status, double Latencia)>(PorCliente);

            for (var i = 0; i < PorCliente; i++)
            {
                var inicio = Stopwatch.GetTimestamp();

                using var resposta = await IngerirAsync($"carga-a-{cliente}-{i}", $"cli-carga-a-{cliente}");

                resultados.Add((resposta.StatusCode, Stopwatch.GetElapsedTime(inicio).TotalMilliseconds));
            }

            return resultados;
        });

        var respostas = (await Task.WhenAll(lotes)).SelectMany(r => r).ToList();
        relogio.Stop();

        var medidas = respostas.Select(r => r.Latencia).Order().ToList();
        var aceitas = respostas.Count(r => r.Status == System.Net.HttpStatusCode.Created);
        var contencao = respostas.Count(r => r.Status == System.Net.HttpStatusCode.ServiceUnavailable);

        Relatar(
            "A — throughput normal",
            $"{Clientes} clientes independentes, {PorCliente} transacoes cada; " +
            $"{aceitas} aceitas, {contencao} em contencao",
            medidas,
            relogio.Elapsed,
            Total);

        // **A medicao mais interessante desta fase esta aqui, e ela contraria a
        // intuicao.** Clientes independentes nao compartilham dado nenhum: a
        // janela historica de um nao inclui as transacoes do outro. Ainda assim
        // parte das requisicoes termina em contencao de serializacao.
        //
        // A causa nao esta no dominio, e sim no plano de consulta. Com a tabela
        // `transacoes` pequena, o PostgreSQL escolhe varredura sequencial — e
        // sob SERIALIZABLE uma varredura sequencial toma predicado sobre a
        // RELACAO INTEIRA, e nao sobre as linhas lidas. Todos passam a conflitar
        // com todos. A Fase 4 ja tinha encontrado isso e mitigado com
        // `SET LOCAL cpu_tuple_cost`; o que esta medicao mostra e que a
        // mitigacao reduz, mas nao elimina, enquanto o volume for baixo.
        //
        // `DesempenhoDeConsultasTests` prova a causa olhando o plano, e
        // `docs/baseline-de-performance.md` registra os numeros. O teste NAO
        // afirma um teto de contencao: seria um numero que depende da maquina,
        // e um limite assim vira teste intermitente.
        Assert.All(
            respostas,
            r => Assert.True(
                r.Status is System.Net.HttpStatusCode.Created
                    or System.Net.HttpStatusCode.ServiceUnavailable,
                $"Status inesperado: {r.Status}. Contencao devolve 503; qualquer 5xx diferente e defeito."));

        // O que precisa valer sempre: nada de meia gravacao. Toda requisicao
        // aceita virou transacao, e nenhuma recusada deixou residuo.
        Assert.Equal(aceitas, await ContarTransacoesAsync("carga-a-"));

        Assert.True(
            Percentil(medidas, 95) < 5_000,
            $"p95 de {Percentil(medidas, 95):0.0} ms acima da ordem de grandeza esperada.");
    }

    // -----------------------------------------------------------------------
    // Cenario B — cliente quente
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cenario_B_o_mesmo_cliente_disputando_consigo_mesmo()
    {
        const int Simultaneas = 20;
        const string Cliente = "cli-carga-b";

        var relogio = Stopwatch.StartNew();

        var envios = Enumerable.Range(0, Simultaneas).Select(async i =>
        {
            var inicio = Stopwatch.GetTimestamp();
            using var resposta = await IngerirAsync($"carga-b-{i}", Cliente);

            return (resposta.StatusCode, Latencia: Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
        });

        var respostas = await Task.WhenAll(envios);
        relogio.Stop();

        var medidas = respostas.Select(r => r.Latencia).Order().ToList();

        Relatar(
            "B — cliente quente",
            $"{Simultaneas} transacoes simultaneas do MESMO cliente",
            medidas,
            relogio.Elapsed,
            Simultaneas);

        // Este e o cenario que o isolamento forte torna caro: todas as
        // requisicoes leem a mesma janela historica, e o PostgreSQL precisa
        // provar que existe uma ordem serial que explique as leituras.
        //
        // O contrato aceita 503: esgotar as tentativas nao e defeito, e sim a
        // disputa durando mais que o orcamento — e repetir com a mesma chave de
        // idempotencia e seguro. O que NAO se aceita e 500.
        Assert.All(
            respostas,
            r => Assert.True(
                r.StatusCode is System.Net.HttpStatusCode.Created
                    or System.Net.HttpStatusCode.ServiceUnavailable,
                $"Status inesperado sob disputa: {r.StatusCode}."));

        var aceitas = respostas.Count(r => r.StatusCode == System.Net.HttpStatusCode.Created);

        Assert.Equal(aceitas, await ContarTransacoesAsync("carga-b-"));
    }

    // -----------------------------------------------------------------------
    // Cenario C — tempestade de idempotencia
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cenario_C_a_mesma_requisicao_repetida_ao_mesmo_tempo()
    {
        const int Repeticoes = 25;

        var chave = $"idem-carga-c-{Guid.NewGuid():N}";
        var corpo = CenarioDeIngestao.Corpo(
            identificadorExterno: "carga-c-unica",
            clienteExternoId: "cli-carga-c",
            ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1));

        var relogio = Stopwatch.StartNew();

        var envios = Enumerable.Range(0, Repeticoes).Select(async _ =>
        {
            var inicio = Stopwatch.GetTimestamp();
            using var resposta = await EnviarAsync(corpo, chave, Cancelamento);

            return (Sucesso: resposta.IsSuccessStatusCode,
                    Latencia: Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
        });

        var respostas = await Task.WhenAll(envios);
        relogio.Stop();

        Relatar(
            "C — tempestade de idempotencia",
            $"{Repeticoes} vezes a MESMA requisicao, ao mesmo tempo",
            respostas.Select(r => r.Latencia).Order().ToList(),
            relogio.Elapsed,
            Repeticoes);

        Assert.All(respostas, r => Assert.True(r.Sucesso));

        // Uma transacao, uma avaliacao, um evento. O custo de vinte e cinco
        // repeticoes precisa ser o custo de UMA — se cada retry disparasse uma
        // avaliacao completa antes de descobrir a duplicata, um cliente com
        // retry agressivo multiplicaria a carga do motor sem gerar nada.
        Assert.Equal(1, await ContarTransacoesAsync("carga-c-"));

        await using var leitura = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        Assert.Single(await leitura.AvaliacoesDeRisco.ToListAsync(Cancelamento));
    }

    // -----------------------------------------------------------------------
    // Cenario D — acumulo assincrono
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cenario_D_um_acumulo_de_eventos_e_drenado_por_completo()
    {
        const int Eventos = 60;

        for (var i = 0; i < Eventos; i++)
        {
            using var resposta = await IngerirAsync($"carga-d-{i}", $"cli-carga-d-{i % 10}");
            resposta.EnsureSuccessStatusCode();
        }

        await using var cenario = CenarioDeMensageria.Criar(_banco.StringDeConexao);

        var relogio = Stopwatch.StartNew();
        var ciclos = await cenario.RodarAteEsvaziarAsync(Cancelamento, maximoDeCiclos: 40);
        relogio.Stop();

        Relatar(
            "D — acumulo assincrono",
            $"{Eventos} eventos represados, drenados em {ciclos} ciclos",
            [],
            relogio.Elapsed,
            Eventos);

        await using var leitura = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        Assert.Empty(await leitura.EventosDeSaida
            .IgnoreQueryFilters()
            .Where(e => e.PublicadoEm == null)
            .ToListAsync(Cancelamento));

        // A recuperacao nao pode deixar residuo: nem mensagem presa na fila,
        // nem mensagem na fila de mortas. Um acumulo grande e exatamente a
        // situacao em que a visibilidade expira no meio do processamento e a
        // mesma mensagem volta — e o consumidor idempotente precisa aguentar.
        Assert.Equal(0, await cenario.Fila.ContarPendentesAsync(
            IFilaDeMensagens.FilaOperacional,
            Cancelamento));

        Assert.Equal(0, await cenario.Fila.ContarMortasAsync(
            IFilaDeMensagens.FilaOperacional,
            Cancelamento));
    }

    // -----------------------------------------------------------------------
    // Auxiliares
    // -----------------------------------------------------------------------

    private static void Relatar(
        string cenario,
        string descricao,
        IReadOnlyList<double> latencias,
        TimeSpan total,
        int operacoes)
    {
        var saida = TestContext.Current.TestOutputHelper;

        if (saida is null)
        {
            return;
        }

        saida.WriteLine($"Cenario {cenario}: {descricao}");
        saida.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  total: {total.TotalMilliseconds:0} ms para {operacoes} operacoes " +
            $"({operacoes / Math.Max(total.TotalSeconds, 0.001):0.0}/s)"));

        if (latencias.Count > 0)
        {
            saida.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  p50: {Percentil(latencias, 50):0.0} ms | " +
                $"p95: {Percentil(latencias, 95):0.0} ms | " +
                $"p99: {Percentil(latencias, 99):0.0} ms"));
        }
    }

    private static double Percentil(IReadOnlyList<double> ordenadas, int percentil)
    {
        if (ordenadas.Count == 0)
        {
            return 0;
        }

        var indice = (int)Math.Ceiling(percentil / 100.0 * ordenadas.Count) - 1;

        return ordenadas[Math.Clamp(indice, 0, ordenadas.Count - 1)];
    }

    private async Task<int> ContarTransacoesAsync(string prefixo)
    {
        await using var leitura = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        return await leitura.Transacoes
            .CountAsync(t => t.IdentificadorExterno.StartsWith(prefixo), Cancelamento);
    }

    private Task<HttpResponseMessage> IngerirAsync(string identificador, string cliente) =>
        EnviarAsync(
            CenarioDeIngestao.Corpo(
                identificadorExterno: identificador,
                clienteExternoId: cliente,
                ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1)),
            $"idem-{Guid.NewGuid():N}",
            Cancelamento);

    private Task<HttpResponseMessage> EnviarAsync(
        object corpo,
        string chaveDeIdempotencia,
        CancellationToken cancellationToken)
    {
        var requisicao = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(CenarioDeIngestao.CaminhoDaIngestao, UriKind.Relative))
        {
            Content = System.Net.Http.Json.JsonContent.Create(corpo),
        };

        requisicao.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{CenarioDeIngestao.EsquemaDaChave} {_integracao.Chave}");

        requisicao.Headers.TryAddWithoutValidation(
            CenarioDeIngestao.CabecalhoDeIdempotencia,
            chaveDeIdempotencia);

        return _cliente.SendAsync(requisicao, cancellationToken);
    }
}
