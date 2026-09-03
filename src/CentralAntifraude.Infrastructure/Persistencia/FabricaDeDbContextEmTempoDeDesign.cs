using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CentralAntifraude.Infrastructure.Persistencia;

/// <summary>
/// Permite que `dotnet ef migrations add` funcione sem subir a API e sem
/// exigir um banco no ar.
///
/// A string de conexao vem da variavel de ambiente quando existe; quando nao
/// existe, usa um alvo local descartavel. Nenhuma credencial real e escrita
/// aqui - gerar migration nao abre conexao.
/// </summary>
public sealed class FabricaDeDbContextEmTempoDeDesign
    : IDesignTimeDbContextFactory<CentralAntifraudeDbContext>
{
    // Alvo descartavel usado apenas quando a variavel de ambiente nao existe.
    // `migrations add` nao abre conexao, entao a senha aqui nunca autentica em
    // lugar nenhum - e escreve-la de forma obviamente falsa evita que alguem a
    // confunda com credencial real.
    private const string ConexaoDeDesignPadrao =
        "Host=localhost;Port=5432;Database=central_antifraude;Username=postgres;Password=sem-uso-em-tempo-de-design";

    public CentralAntifraudeDbContext CreateDbContext(string[] args)
    {
        var conexao =
            Environment.GetEnvironmentVariable($"ConnectionStrings__{OpcoesDoDbContext.NomeDaConexao}")
            ?? ConexaoDeDesignPadrao;

        var construtor = new DbContextOptionsBuilder<CentralAntifraudeDbContext>();
        OpcoesDoDbContext.Configurar(construtor, conexao);

        return new CentralAntifraudeDbContext(construtor.Options);
    }
}
