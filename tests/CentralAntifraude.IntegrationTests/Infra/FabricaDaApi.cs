using CentralAntifraude.Application.Identidade;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CentralAntifraude.IntegrationTests.Infra;

/// <summary>
/// Hospeda a API real - o mesmo Program.cs de producao - em memoria.
///
/// O ambiente e Production de proposito: e o contrato de producao que precisa
/// ser verificado. Rodar os testes em Development esconderia justamente o que
/// mais importa, que e o que a API revela quando algo da errado la fora.
/// </summary>
public sealed class FabricaDaApi : WebApplicationFactory<Program>
{
    /// <summary>
    /// Chave de assinatura descartavel, sorteada por instancia. Nao ha valor
    /// fixo de token no repositorio, e um token emitido em um teste nao vale
    /// em outro.
    /// </summary>
    private readonly string _chaveDeAssinatura =
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

    /// <summary>Chave de fingerprint de IP, tambem sorteada por instancia.</summary>
    private readonly string _chaveDeFingerprint =
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

    /// <summary>
    /// Log do host de teste, em memoria.
    ///
    /// Sem ele, uma falha de autenticacao aparece apenas como 401 e o teste
    /// nao diz por que - o handler de autenticacao nao escreve o motivo na
    /// resposta, de proposito.
    /// </summary>
    /// <summary>
    /// Uma fotografia das linhas de log, em texto. Cada leitura devolve uma
    /// copia: ha sempre alguma requisicao escrevendo enquanto o teste le.
    /// </summary>
    public IReadOnlyList<string> Registros => Logs.Texto;

    /// <summary>
    /// As mesmas linhas, com nivel, EventId e propriedades estruturadas —
    /// inclusive as herdadas de escopo. E o que permite afirmar que uma linha
    /// carrega `CorrelationId`, em vez de procurar o valor dentro do texto.
    /// </summary>
    public ColetorDeLogs Logs { get; }

    private readonly string _stringDeConexao;
    private readonly IReadOnlyDictionary<string, string> _ajustes;

    /// <summary>
    /// Hospeda a API com configuracao extra.
    ///
    /// Existe para os testes de CORS e de limite: as duas coisas dependem de
    /// configuracao que o resto da suite nao quer ligada — origens permitidas
    /// mudam o comportamento de toda requisicao, e um teste de rate limit
    /// gastaria a cota das outras classes se dividisse o mesmo host.
    /// </summary>
    public FabricaDaApi(
        string stringDeConexao,
        IReadOnlyDictionary<string, string>? ajustes = null)
    {
        _stringDeConexao = stringDeConexao;
        _ajustes = ajustes ?? new Dictionary<string, string>(StringComparer.Ordinal);
        Logs = new ColetorDeLogs();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Production);
        builder.UseSetting("ConnectionStrings:Postgres", _stringDeConexao);
        builder.UseSetting($"{OpcoesDeAutenticacao.Secao}:ChaveDeAssinatura", _chaveDeAssinatura);
        builder.UseSetting(
            $"{CentralAntifraude.Application.Transacoes.OpcoesDeIngestao.Secao}:ChaveDeFingerprint",
            _chaveDeFingerprint);

        // Sem isto, a regra de Logging:LogLevel do appsettings filtra Debug e
        // o motivo de uma recusa de autenticacao some do diagnostico.
        builder.UseSetting("Logging:LogLevel:Default", "Debug");

        // O appsettings silencia os comandos do EF Core em Warning, o que
        // e certo em producao e cego no teste: e por estas linhas que a
        // baseline conta quantas consultas a operacao critica dispara, e
        // que um N+1 introduzido por engano aparece.
        builder.UseSetting(
            "Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command",
            "Information");

        foreach (var (chave, valor) in _ajustes)
        {
            builder.UseSetting(chave, valor);
        }

        builder.ConfigureLogging(log =>
        {
            log.ClearProviders();
            log.AddProvider(Logs);
            log.SetMinimumLevel(LogLevel.Debug);
        });

        builder.ConfigureTestServices(servicos =>
        {
            // Custo de hash reduzido SO nos testes. As 220.000 iteracoes de
            // producao somariam minutos ao longo da suite sem provar nada
            // sobre comportamento - e o valor real e verificado por um teste
            // unitario dedicado.
            servicos.Configure<PasswordHasherOptions>(o => o.IterationCount = 1_000);
        });
    }

    /// <summary>
    /// Cliente que NAO gerencia cookies sozinho.
    ///
    /// Por padrao o WebApplicationFactory guarda os cookies num container e os
    /// reenvia automaticamente. Isso e conveniente para simular um navegador,
    /// mas inutiliza os testes de rotacao e reuso: o cliente mandaria sempre o
    /// cookie mais recente, e nunca o antigo que o teste quer reapresentar.
    ///
    /// Com o controle na mao do teste, cada requisicao leva exatamente o
    /// cookie que o cenario exige.
    /// </summary>
    public HttpClient CriarClienteSemCookieAutomatico() =>
        CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

}
