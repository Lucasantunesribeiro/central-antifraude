using CentralAntifraude.IntegrationTests.Infra;
using CentralAntifraude.Infrastructure.Identidade;
using CentralAntifraude.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Prova que a cadeia EF Core -> Npgsql -> PostgreSQL real funciona e que a
/// linha de base de migrations e reproduzivel (ROADMAP secao 0.11).
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class MigrationsTests
{
    private readonly FixtureDoBanco _banco;

    public MigrationsTests(FixtureDoBanco banco)
    {
        _banco = banco;
    }

    [Fact]
    public async Task Migrations_aplicam_do_zero_em_um_banco_novo()
    {
        await using var contexto = CriarContexto();

        await contexto.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var aplicadas = await contexto.Database
            .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(aplicadas, migration => migration.EndsWith("InicialBaseline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Aplicar_migrations_de_novo_nao_quebra_nem_duplica()
    {
        // O deploy pode rodar a migracao mais de uma vez (retry, duas
        // instancias subindo juntas). Aplicar duas vezes tem que ser inofensivo.
        await using var contexto = CriarContexto();

        await contexto.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var primeiraLeitura = await contexto.Database
            .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);

        await contexto.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var segundaLeitura = await contexto.Database
            .GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(primeiraLeitura, segundaLeitura);
    }

    [Fact]
    public async Task Nao_ha_alteracao_de_modelo_pendente_de_migration()
    {
        // Guarda de verdade para as proximas fases: se alguem adicionar uma
        // entidade e esquecer de gerar a migration, o CI quebra aqui - e nao
        // em producao, com o schema fora de sincronia com o codigo.
        await using var contexto = CriarContexto();

        await contexto.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.False(
            contexto.Database.HasPendingModelChanges(),
            "O modelo do EF Core mudou sem migration correspondente. " +
            "Rode: dotnet dotnet-ef migrations add <Nome> --project src/CentralAntifraude.Infrastructure");
    }

    [Fact]
    public async Task Convencao_de_nomes_snake_case_chega_ao_banco()
    {
        // Verifica no banco real, nao na configuracao: o que importa e como a
        // coluna ficou, nao o que o construtor de opcoes prometeu.
        await using var contexto = CriarContexto();
        await contexto.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var conexao = contexto.Database.GetDbConnection();
        await conexao.OpenAsync(TestContext.Current.CancellationToken);

        await using var comando = conexao.CreateCommand();
        comando.CommandText =
            """
            select count(*)
            from information_schema.columns
            where table_name = '__EFMigrationsHistory'
              and column_name = 'migration_id'
            """;

        var encontradas = await comando.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1L, Convert.ToInt64(encontradas, System.Globalization.CultureInfo.InvariantCulture));
    }

    private CentralAntifraudeDbContext CriarContexto()
    {
        var construtor = new DbContextOptionsBuilder<CentralAntifraudeDbContext>();
        OpcoesDoDbContext.Configurar(construtor, _banco.StringDeConexao);

        return new CentralAntifraudeDbContext(construtor.Options, ContextoDeUsuarioFixo.Anonimo);
    }
}
