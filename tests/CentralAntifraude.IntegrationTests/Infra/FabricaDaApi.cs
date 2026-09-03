using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    }
}
