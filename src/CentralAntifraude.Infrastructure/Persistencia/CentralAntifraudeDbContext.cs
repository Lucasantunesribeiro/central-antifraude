using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Integracoes;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Transacoes;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.Infrastructure.Persistencia;

/// <summary>
/// Contexto de persistencia unico da Central Antifraude.
/// </summary>
public class CentralAntifraudeDbContext : DbContext
{
    /// <summary>
    /// Nome do filtro global de tenant.
    ///
    /// Nomeado (recurso do EF Core 10) para que possa ser desligado
    /// individualmente com <c>IgnoreQueryFilters([FiltroDeTenant])</c>,
    /// deixando qualquer outro filtro futuro — exclusao logica, por exemplo —
    /// ainda ativo. Desligar "todos os filtros" para atingir um so seria um
    /// jeito silencioso de abrir uma porta que ninguem queria abrir.
    /// </summary>
    public const string FiltroDeTenant = "Tenant";

    private readonly IContextoDoUsuarioAtual _contexto;

    public CentralAntifraudeDbContext(
        DbContextOptions<CentralAntifraudeDbContext> opcoes,
        IContextoDoUsuarioAtual contexto)
        : base(opcoes)
    {
        _contexto = contexto;
    }

    public DbSet<Organizacao> Organizacoes => Set<Organizacao>();

    public DbSet<Usuario> Usuarios => Set<Usuario>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<RegistroDeAuditoria> RegistrosDeAuditoria => Set<RegistroDeAuditoria>();

    public DbSet<Integracao> Integracoes => Set<Integracao>();

    public DbSet<CredencialDeIntegracao> CredenciaisDeIntegracao => Set<CredencialDeIntegracao>();

    public DbSet<Transacao> Transacoes => Set<Transacao>();

    public DbSet<Regra> Regras => Set<Regra>();

    public DbSet<VersaoDeRegra> VersoesDeRegra => Set<VersaoDeRegra>();

    public DbSet<PerfilDeRisco> PerfisDeRisco => Set<PerfilDeRisco>();

    public DbSet<VersaoDePerfilDeRisco> VersoesDePerfilDeRisco => Set<VersaoDePerfilDeRisco>();

    public DbSet<AvaliacaoDeRisco> AvaliacoesDeRisco => Set<AvaliacaoDeRisco>();

    /// <summary>
    /// Tenant efetivo da requisicao atual.
    ///
    /// Lido a cada consulta, e nao capturado na construcao do modelo: o EF
    /// transforma o acesso a este membro em parametro da consulta, entao o
    /// modelo compilado e cacheado continua correto para qualquer usuario.
    ///
    /// Vale <see cref="Guid.Empty"/> quando nao ha identidade — e nenhuma
    /// linha tem organizacao vazia, entao o resultado e "nada", e nao "tudo".
    /// </summary>
    private Guid OrganizacaoAtual => _contexto.OrganizacaoId;

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

        // ---------------------------------------------------------------
        // Isolamento de tenant.
        //
        // Esta e a defesa que nao depende de ninguem lembrar de escrever
        // `.Where(x => x.OrganizacaoId == ...)`. Uma consulta futura escrita
        // sem o filtro continua isolada; sem isto, bastaria um esquecimento
        // em uma tela para vazar dados entre clientes.
        //
        // Nao substitui os testes de isolamento — e a segunda camada deles.
        // ---------------------------------------------------------------
        modelBuilder.Entity<Usuario>()
            .HasQueryFilter(FiltroDeTenant, u => u.OrganizacaoId == OrganizacaoAtual);

        modelBuilder.Entity<RefreshToken>()
            .HasQueryFilter(FiltroDeTenant, t => t.OrganizacaoId == OrganizacaoAtual);

        modelBuilder.Entity<RegistroDeAuditoria>()
            .HasQueryFilter(FiltroDeTenant, r => r.OrganizacaoId == OrganizacaoAtual);

        modelBuilder.Entity<Integracao>()
            .HasQueryFilter(FiltroDeTenant, i => i.OrganizacaoId == OrganizacaoAtual);

        modelBuilder.Entity<CredencialDeIntegracao>()
            .HasQueryFilter(FiltroDeTenant, c => c.OrganizacaoId == OrganizacaoAtual);

        modelBuilder.Entity<Transacao>()
            .HasQueryFilter(FiltroDeTenant, t => t.OrganizacaoId == OrganizacaoAtual);

        modelBuilder.Entity<Regra>()
            .HasQueryFilter(FiltroDeTenant, r => r.OrganizacaoId == OrganizacaoAtual);

        modelBuilder.Entity<VersaoDeRegra>()
            .HasQueryFilter(FiltroDeTenant, v => v.OrganizacaoId == OrganizacaoAtual);

        modelBuilder.Entity<PerfilDeRisco>()
            .HasQueryFilter(FiltroDeTenant, p => p.OrganizacaoId == OrganizacaoAtual);

        modelBuilder.Entity<VersaoDePerfilDeRisco>()
            .HasQueryFilter(FiltroDeTenant, v => v.OrganizacaoId == OrganizacaoAtual);

        modelBuilder.Entity<AvaliacaoDeRisco>()
            .HasQueryFilter(FiltroDeTenant, a => a.OrganizacaoId == OrganizacaoAtual);

        // O sinal tambem carrega a organizacao e tambem e filtrado. Ele so e
        // alcancado atraves da avaliacao, que ja esta filtrada — mas a defesa
        // dupla custa nada e sobrevive a uma consulta futura que parta direto
        // do sinal.
        modelBuilder.Entity<SinalDeRisco>()
            .HasQueryFilter(FiltroDeTenant, s => s.OrganizacaoId == OrganizacaoAtual);

        // Organizacao nao recebe filtro: ela e o tenant, nao pertence a um.
        // O acesso a ela e sempre por identificador ja derivado da identidade.

        base.OnModelCreating(modelBuilder);
    }
}
