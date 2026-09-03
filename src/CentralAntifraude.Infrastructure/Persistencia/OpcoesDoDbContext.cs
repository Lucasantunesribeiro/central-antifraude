using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.Infrastructure.Persistencia;

/// <summary>
/// Montagem das opcoes do DbContext em um lugar so.
///
/// Existe porque tres consumidores precisam da MESMA configuracao e nao podem
/// divergir: a API, a fabrica de tempo de design (que gera as migrations) e
/// os testes de integracao contra PostgreSQL real. Se cada um montasse as
/// opcoes por conta propria, uma migration poderia ser gerada com convencao
/// de nomes diferente da que a aplicacao usa em execucao.
/// </summary>
public static class OpcoesDoDbContext
{
    /// <summary>Nome da string de conexao em IConfiguration.</summary>
    public const string NomeDaConexao = "Postgres";

    public static void Configurar(
        DbContextOptionsBuilder construtor,
        string stringDeConexao)
    {
        ArgumentNullException.ThrowIfNull(construtor);
        ArgumentException.ThrowIfNullOrWhiteSpace(stringDeConexao);

        construtor
            .UseNpgsql(
                stringDeConexao,
                npgsql => npgsql.MigrationsAssembly(
                    typeof(CentralAntifraudeDbContext).Assembly.GetName().Name))
            // Nomes em snake_case sem aspas: um analista ou auditor abrindo o
            // psql durante uma investigacao consegue escrever a consulta sem
            // citar cada identificador. Convencao nativa do PostgreSQL.
            .UseSnakeCaseNamingConvention();
    }
}
