using CentralAntifraude.Application.Identidade;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

    private readonly string _stringDeConexao;

    public FabricaDaApi(string stringDeConexao)
    {
        _stringDeConexao = stringDeConexao;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Production);
        builder.UseSetting("ConnectionStrings:Postgres", _stringDeConexao);
        builder.UseSetting($"{OpcoesDeAutenticacao.Secao}:ChaveDeAssinatura", _chaveDeAssinatura);

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
