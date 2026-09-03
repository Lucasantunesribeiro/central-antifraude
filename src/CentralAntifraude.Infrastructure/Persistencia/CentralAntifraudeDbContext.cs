using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.Infrastructure.Persistencia;

/// <summary>
/// Contexto de persistencia unico da Central Antifraude.
///
/// A Fase 0 nao possui nenhuma entidade de dominio - de proposito. O que
/// existe aqui sao as convencoes que valerao para todas as entidades das
/// fases seguintes, e a linha de base de migrations.
/// </summary>
public class CentralAntifraudeDbContext : DbContext
{
    public CentralAntifraudeDbContext(DbContextOptions<CentralAntifraudeDbContext> opcoes)
        : base(opcoes)
    {
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Dinheiro nunca perde centavo por arredondamento de coluna:
        // numeric(18,4) cobre valores de pagamento com folga de escala.
        // CLAUDE.md secao 101.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);

        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CentralAntifraudeDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
