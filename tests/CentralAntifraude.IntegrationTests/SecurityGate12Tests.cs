using System.Text.RegularExpressions;
using CentralAntifraude.Application.Observabilidade;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Security Gate 12 — observabilidade e uma superficie, e nao so um beneficio.
///
/// Toda fase anterior perguntou "quem pode chamar esta rota?". Esta pergunta
/// outra coisa: **o que acabou de sair do banco sem passar por autorizacao
/// nenhuma?**
///
/// Log e metrica escapam do perimetro que o produto inteiro defende. O dado no
/// banco tem filtro de tenant, politica de perfil e trilha de auditoria; a
/// mesma informacao dentro de uma linha de log tem apenas as permissoes do
/// coletor. Um termo de busca digitado por um analista, um identificador de
/// cliente ou um score copiado "so para facilitar o diagnostico" atravessam
/// essa fronteira sem que nada reclame.
///
/// Por isso a Fase 12, que ACRESCENTA observabilidade, precisa de gate proprio:
/// ela e a unica fase que cria caminho novo para o dado sair.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed partial class SecurityGate12Tests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;
    private ColetorDeMetricas _metricas = null!;

    public SecurityGate12Tests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"gate12-{Guid.NewGuid():N}"[..14],
            Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();
        _integracao = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenant, Cancelamento);
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

    [Fact]
    public async Task O_que_o_analista_digita_na_busca_nunca_entra_no_log()
    {
        var termo = $"maria-de-lourdes-{Guid.NewGuid():N}"[..28];

        var token = await CenarioDeIngestao.TokenDeAsync(
            _cliente,
            _tenant,
            PerfilDeUsuario.AnalistaDeFraude,
            Cancelamento);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Get,
            $"/api/transacoes?busca={Uri.EscapeDataString(termo)}",
            token);

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);
        resposta.EnsureSuccessStatusCode();

        // **Esta e a razao de a operacao ser o padrao da rota e nao a URL.** A
        // busca do console aceita identificador externo, cliente e dispositivo:
        // quem digita ali esta digitando dado de investigacao. Registrar a query
        // string "para facilitar o diagnostico" mandaria isso para fora do
        // banco, onde o filtro de tenant e a politica de perfil nao alcancam.
        Assert.DoesNotContain(
            _fabrica.Logs.Entradas,
            e => e.Mensagem.Contains(termo, StringComparison.OrdinalIgnoreCase) ||
                 e.Propriedades.Values.Any(v => v.Contains(termo, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Nenhuma_dimensao_de_metrica_carrega_identificador()
    {
        using (var resposta = await IngerirAsync("gate12-metrica"))
        {
            resposta.EnsureSuccessStatusCode();
        }

        var valores = _metricas.Medidas
            .SelectMany(m => m.Etiquetas.Values)
            .ToList();

        // A lista de dimensoes permitidas defende o NOME da dimensao. Isto
        // defende o VALOR: alguem poderia declarar `resultado` — nome
        // impecavel — e gravar ali um identificador de transacao. O efeito
        // seria o mesmo: uma serie de metrica por recurso, e um identificador
        // de tenant num sistema que nao tem tenant.
        Assert.DoesNotContain(valores, v => PareceIdentificador().IsMatch(v));
    }

    [Fact]
    public async Task O_valor_de_correlacao_enviado_pelo_cliente_nao_injeta_conteudo_no_log()
    {
        // Uma quebra de linha seguida de texto forjado faria a linha seguinte
        // do log parecer uma mensagem legitima do sistema — e quem investiga
        // acreditaria nela. Por isso o formato aceito e fechado desde a Fase 0;
        // aqui se verifica que a defesa continua valendo depois de a correlacao
        // passar a viajar por escopo e a ser reposta pelo worker.
        var forjado = "abcdefgh\n[Error] Sistema comprometido";

        using var requisicao = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/health/live", UriKind.Relative));

        requisicao.Headers.TryAddWithoutValidation("X-Correlation-Id", forjado);

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);
        resposta.EnsureSuccessStatusCode();

        var devolvido = resposta.Headers.GetValues("X-Correlation-Id").Single();

        Assert.DoesNotContain('\n', devolvido);
        Assert.NotEqual(forjado, devolvido);

        Assert.DoesNotContain(
            _fabrica.Logs.Entradas,
            e => e.Propriedade("CorrelationId")?.Contains("comprometido", StringComparison.Ordinal) ?? false);
    }

    [Fact]
    public async Task A_falha_de_dependencia_fica_no_log_e_nao_na_resposta()
    {
        var conexaoMorta =
            "Host=127.0.0.1;Port=59986;Database=inexistente;Username=ninguem;Timeout=2";

        await using var fabricaSemBanco = new FabricaDaApi(conexaoMorta);
        using var cliente = fabricaSemBanco.CriarClienteSemCookieAutomatico();

        using var resposta = await cliente.GetAsync(
            new Uri("/health/ready", UriKind.Relative),
            Cancelamento);

        var corpo = await resposta.Content.ReadAsStringAsync(Cancelamento);

        // As duas metades da mesma decisao. A resposta e publica e diz o
        // minimo; o log e interno e diz o suficiente para consertar. Trocar
        // isso de lugar produz os dois erros classicos ao mesmo tempo: um
        // endpoint de saude que entrega topologia a quem perguntar, e uma
        // equipe sem nenhuma pista do que quebrou.
        Assert.DoesNotContain("59986", corpo, StringComparison.Ordinal);

        Assert.Contains(
            fabricaSemBanco.Logs.Entradas,
            e => e.Mensagem.Contains("59986", StringComparison.Ordinal) ||
                 e.Mensagem.Contains("connect", StringComparison.OrdinalIgnoreCase) ||
                 e.Mensagem.Contains("conex", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Nenhum_medidor_do_produto_escapa_do_catalogo()
    {
        using (var resposta = await IngerirAsync("gate12-catalogo"))
        {
            resposta.EnsureSuccessStatusCode();
        }

        // Um medidor criado fora do catalogo nao seria exportado na Fase 14 e
        // nao passaria pela revisao de cardinalidade — existiria emitindo
        // series que ninguem declarou. O prefixo compartilhado e o que permite
        // que este teste, e o exportador, encontrem tudo.
        var medidores = _metricas.Medidas
            .Select(m => m.Medidor)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(medidores, nome => Assert.Contains(nome, Telemetria.Medidores));
    }

    private Task<HttpResponseMessage> IngerirAsync(string identificador)
    {
        var requisicao = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(CenarioDeIngestao.CaminhoDaIngestao, UriKind.Relative))
        {
            Content = System.Net.Http.Json.JsonContent.Create(CenarioDeIngestao.Corpo(
                identificadorExterno: identificador,
                clienteExternoId: $"cli-{identificador}",
                ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1))),
        };

        requisicao.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{CenarioDeIngestao.EsquemaDaChave} {_integracao.Chave}");

        requisicao.Headers.TryAddWithoutValidation(
            CenarioDeIngestao.CabecalhoDeIdempotencia,
            $"idem-{Guid.NewGuid():N}");

        return _cliente.SendAsync(requisicao, Cancelamento);
    }

    /// <summary>GUID, ULID ou qualquer sequencia longa de hexadecimal.</summary>
    [GeneratedRegex(
        "[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant)]
    private static partial Regex PareceIdentificador();
}
