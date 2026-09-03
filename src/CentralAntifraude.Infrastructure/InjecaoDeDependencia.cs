using CentralAntifraude.Domain.Tempo;
using CentralAntifraude.Infrastructure.Persistencia;
using CentralAntifraude.Infrastructure.Tempo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CentralAntifraude.Infrastructure;

/// <summary>
/// Ponto unico de registro da infraestrutura.
///
/// A API chama este metodo e nao precisa referenciar EF Core nem Npgsql -
/// e o que mantem a persistencia confinada a este projeto, condicao
/// verificada pelos testes de arquitetura.
/// </summary>
public static class InjecaoDeDependencia
{
    /// <summary>Nome do health check de banco exposto em /health/ready.</summary>
    public const string NomeDoHealthCheckDeBanco = "postgresql";

    /// <summary>Tag que separa dependencias externas do liveness do processo.</summary>
    public const string TagDeProntidao = "pronto";

    public static IServiceCollection AdicionarInfraestrutura(
        this IServiceCollection servicos,
        IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(servicos);
        ArgumentNullException.ThrowIfNull(configuracao);

        var stringDeConexao = configuracao.GetConnectionString(OpcoesDoDbContext.NomeDaConexao);

        // Falha fechada: sem banco configurado a aplicacao nao sobe pela
        // metade. Um processo no ar que responde 200 no liveness e falha em
        // toda avaliacao de risco e pior do que um processo que nao subiu.
        if (string.IsNullOrWhiteSpace(stringDeConexao))
        {
            throw new InvalidOperationException(
                $"String de conexao '{OpcoesDoDbContext.NomeDaConexao}' nao configurada. " +
                "Defina a variavel de ambiente " +
                $"ConnectionStrings__{OpcoesDoDbContext.NomeDaConexao} " +
                "ou use `dotnet user-secrets`. Ver docs/setup-local.md.");
        }

        servicos.AddDbContext<CentralAntifraudeDbContext>(
            opcoes => OpcoesDoDbContext.Configurar(opcoes, stringDeConexao));

        servicos.AddSingleton<IRelogio, RelogioSistema>();

        servicos
            .AddHealthChecks()
            .AddDbContextCheck<CentralAntifraudeDbContext>(
                name: NomeDoHealthCheckDeBanco,
                tags: [TagDeProntidao]);

        return servicos;
    }
}
