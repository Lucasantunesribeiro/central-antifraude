using System.Globalization;
using System.Net;
using System.Text.Json;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// A operacao critica sob concorrencia real, contra PostgreSQL real.
///
/// **O que se prova aqui nao pode ser provado de outro jeito.** A regra de
/// velocidade le um CONJUNTO — "quantas transacoes deste cliente na janela" — e
/// decide a partir dele. Duas requisicoes simultaneas, cada uma sem enxergar a
/// outra, produziriam duas avaliacoes que nenhuma execucao sequencial
/// produziria. Nenhum mock reproduz isso; so um banco com isolamento de
/// verdade.
///
/// **A promessa que o sistema faz** (ROADMAP 4.5): o resultado equivale a
/// ALGUMA ordem serial valida. Nao se exige que toda requisicao enxergue as
/// simultaneas — isso seria exigir que enxergasse o futuro.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class ConcorrenciaTests : IAsyncLifetime
{
    /// <summary>Limite do catalogo padrao: mais de 3 em 10 minutos dispara.</summary>
    private const int LimiteDeVelocidade = 3;

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;

    public ConcorrenciaTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"conc-{Guid.NewGuid():N}"[..14],
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
    // Regra de velocidade sob concorrencia
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Rajada_simultanea_produz_contagem_equivalente_a_uma_ordem_serial()
    {
        // Seis transacoes do MESMO cliente, com o MESMO OcorridaEm, enviadas
        // ao mesmo tempo.
        //
        // O mesmo instante e o que torna a asserção exata. Como a janela da
        // regra inclui tudo com OcorridaEm <= o da avaliada, cada transacao
        // enxerga exatamente as que ja estavam gravadas quando ela avaliou.
        // Em QUALQUER ordem serial valida, a k-esima ve k-1 anteriores e conta
        // k tentativas na janela.
        //
        // Portanto o conjunto de contagens tem que ser exatamente
        // {1, 2, 3, 4, 5, 6} - sem repetido e sem buraco. Uma contagem
        // repetida significaria duas transacoes que nao enxergaram uma a
        // outra: leitura perdida, e nenhuma ordem serial explicaria o
        // resultado. Um buraco significaria transacao sumida do contexto.
        const int Simultaneas = 6;
        var cliente = $"cli-serial-{Guid.NewGuid():N}"[..20];
        var instante = DateTimeOffset.UtcNow.AddMinutes(-1);

        var respostas = await Task.WhenAll(
            Enumerable.Range(0, Simultaneas).Select(i => EnviarAsync(CenarioDeIngestao.Corpo(
                identificadorExterno: $"serial-{i}",
                clienteExternoId: cliente,
                ocorridaEm: instante))));

        foreach (var resposta in respostas)
        {
            Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
            resposta.Dispose();
        }

        var (contagensComSinal, semSinal) = await ContagensNaJanelaAsync(cliente);

        // As que passaram do limite registraram a contagem exata que
        // enxergaram. Em qualquer ordem serial valida, so podem ser 4, 5 e 6.
        Assert.Equal([4, 5, 6], contagensComSinal.Order().ToList());

        // E as tres restantes ficaram dentro do limite - as posicoes 1, 2 e 3.
        Assert.Equal(LimiteDeVelocidade, semSinal);
    }

    [Fact]
    public async Task Rajada_simultanea_dispara_a_regra_nas_transacoes_certas()
    {
        // Consequencia direta do teste anterior, verificada pelo lado do
        // produto: com limite 3 e seis tentativas, exatamente tres passam do
        // limite. Nem uma a mais (a regra nao pode disparar cedo demais e
        // encher a fila do analista), nem uma a menos (fraude nao pode passar
        // porque duas requisicoes chegaram juntas).
        const int Simultaneas = 6;
        var cliente = $"cli-disparo-{Guid.NewGuid():N}"[..20];
        var instante = DateTimeOffset.UtcNow.AddMinutes(-1);

        var respostas = await Task.WhenAll(
            Enumerable.Range(0, Simultaneas).Select(i => EnviarAsync(CenarioDeIngestao.Corpo(
                identificadorExterno: $"disparo-{i}",
                clienteExternoId: cliente,
                ocorridaEm: instante))));

        foreach (var resposta in respostas)
        {
            resposta.EnsureSuccessStatusCode();
            resposta.Dispose();
        }

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var comSinal = await contexto.AvaliacoesDeRisco
            .Where(a => contexto.Transacoes
                .Any(t => t.Id == a.TransacaoId && t.ClienteExternoId == cliente))
            .SelectMany(a => a.Sinais)
            .Where(s => s.Tipo == TipoDeRegra.VelocidadePorCliente)
            .CountAsync(Cancelamento);

        Assert.Equal(Simultaneas - LimiteDeVelocidade, comSinal);
    }

    [Fact]
    public async Task Clientes_diferentes_em_paralelo_nao_interferem_entre_si()
    {
        // Concorrencia so precisa ser resolvida quando ha disputa de verdade.
        // Doze requisicoes de doze clientes distintos nao compartilham
        // contexto: cada uma sai com score 0, e o isolamento forte nao pode
        // inventar conflito onde nao existe.
        const int Clientes = 12;
        var marca = $"{Guid.NewGuid():N}"[..8];

        var respostas = await Task.WhenAll(
            Enumerable.Range(0, Clientes).Select(i => EnviarAsync(CenarioDeIngestao.Corpo(
                identificadorExterno: $"paralelo-{marca}-{i}",
                clienteExternoId: $"cli-{marca}-{i}",
                ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1)))));

        // Todas precisam ser criadas. Um 503 aqui significaria que o
        // isolamento forte inventou conflito onde nao existe disputa - e foi
        // exatamente isso que a medicao desta fase encontrou e corrigiu.
        foreach (var resposta in respostas)
        {
            Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);

            using var json = JsonDocument.Parse(
                await resposta.Content.ReadAsStringAsync(Cancelamento));

            Assert.Equal(0, json.RootElement.GetProperty("score").GetInt32());

            resposta.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Duplicidade sob corrida
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Vinte_requisicoes_identicas_produzem_uma_transacao_e_uma_avaliacao()
    {
        // A corrida classica, agora sob isolamento forte. A terceira camada de
        // idempotencia mudou de forma: quem perde o INSERT nao consegue achar
        // a vencedora reconsultando dentro da mesma transacao, porque o
        // snapshot dela e anterior ao commit da outra. Quem resolve e o retry
        // da operacao inteira, com snapshot novo.
        var corpo = CenarioDeIngestao.Corpo(
            identificadorExterno: "corrida-serializavel",
            clienteExternoId: $"cli-corrida-{Guid.NewGuid():N}"[..20]);

        var chave = $"idem-{Guid.NewGuid():N}";

        var respostas = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => EnviarAsync(corpo, chave)));

        var criadas = respostas.Count(r => r.StatusCode == HttpStatusCode.Created);
        var repetidas = respostas.Count(r => r.StatusCode == HttpStatusCode.OK);

        Assert.Equal(1, criadas);
        Assert.Equal(19, repetidas);

        var transacaoId = await CenarioDeIngestao.IdDaTransacaoAsync(
            respostas.First(r => r.StatusCode == HttpStatusCode.Created));

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        Assert.Equal(
            1,
            await contexto.AvaliacoesDeRisco.CountAsync(a => a.TransacaoId == transacaoId, Cancelamento));

        // E um evento so. A Outbox nao pode multiplicar o fato "esta transacao
        // foi avaliada" por causa dos retries do integrador - cada evento a
        // mais vira um efeito duplicado la na frente. O tenant deste teste e
        // novo, entao a conta e direta: uma transacao, um evento.
        Assert.Equal(1, await contexto.Transacoes.CountAsync(Cancelamento));
        Assert.Equal(1, await contexto.EventosDeSaida.CountAsync(Cancelamento));

        foreach (var resposta in respostas)
        {
            resposta.Dispose();
        }
    }

    [Fact]
    public async Task O_retry_de_concorrencia_realmente_dispara_e_e_registrado()
    {
        // Sem esta verificacao, os testes acima poderiam estar passando porque
        // a maquina serializou as requisicoes por acaso - e o mecanismo de
        // retry nunca teria sido exercitado.
        //
        // O log estruturado e a evidencia: ele registra tentativa e motivo,
        // e nunca payload, credencial ou identificador de cliente.
        var cliente = $"cli-retry-{Guid.NewGuid():N}"[..20];
        var instante = DateTimeOffset.UtcNow.AddMinutes(-1);

        var respostas = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => EnviarAsync(CenarioDeIngestao.Corpo(
                identificadorExterno: $"retry-{i}",
                clienteExternoId: cliente,
                ocorridaEm: instante))));

        foreach (var resposta in respostas)
        {
            resposta.Dispose();
        }

        var registros = _fabrica.Registros;

        var refeitas = registros
            .Where(r => r.Contains("Operacao critica refeita", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(refeitas);

        // O motivo aparece; dado de cliente, nao.
        Assert.All(refeitas, linha =>
        {
            Assert.Contains("motivo", linha, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(cliente, linha, StringComparison.Ordinal);
        });
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
    /// As contagens que as avaliacoes REGISTRARAM, e quantas nao dispararam.
    ///
    /// So quem passou do limite grava a contagem, na evidencia do sinal. O
    /// resto do conjunto nao e inventado aqui: o teste verifica os valores
    /// gravados e o numero das que ficaram caladas, e essas duas coisas juntas
    /// so fecham se cada transacao enxergou um numero diferente de anteriores.
    /// </summary>
    private async Task<(List<int> ComSinal, int SemSinal)> ContagensNaJanelaAsync(string cliente)
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var transacoes = await contexto.Transacoes
            .Where(t => t.ClienteExternoId == cliente)
            .Select(t => t.Id)
            .ToListAsync(Cancelamento);

        var avaliacoes = await contexto.AvaliacoesDeRisco
            .Where(a => transacoes.Contains(a.TransacaoId))
            .ToListAsync(Cancelamento);

        var contagens = new List<int>();
        var semSinal = 0;

        foreach (var avaliacao in avaliacoes)
        {
            var sinal = avaliacao.Sinais
                .FirstOrDefault(s => s.Tipo == TipoDeRegra.VelocidadePorCliente);

            if (sinal is null)
            {
                semSinal++;
                continue;
            }

            contagens.Add(int.Parse(
                sinal.DadosDaEvidencia["tentativasNaJanela"],
                CultureInfo.InvariantCulture));
        }

        return (contagens, semSinal);
    }
}
