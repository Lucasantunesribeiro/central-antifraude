using Testcontainers.PostgreSql;

namespace CentralAntifraude.IntegrationTests.Infra;

/// <summary>
/// PostgreSQL real, em container, compartilhado pelos testes de integracao.
///
/// CLAUDE.md secao 84 proibe SQLite como substituto: as fases seguintes
/// dependem de semantica que so o PostgreSQL tem - SERIALIZABLE, FOR UPDATE
/// SKIP LOCKED, constraints parciais. Um teste verde no SQLite nao prova nada
/// sobre o banco que roda em producao.
/// </summary>
public sealed class FixtureDoBanco : IAsyncLifetime
{
    // Mesma versao maior do docker-compose local e do Neon planejado para a
    // Fase 14: paridade entre desenvolvimento, teste e producao.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("central_antifraude_testes")
        .WithUsername("testes")
        // Credencial descartavel, gerada por execucao e viva so enquanto o
        // container existe. Nenhum valor fixo no repositorio.
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public string StringDeConexao => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Nome)]
public sealed class ColecaoDoBanco : ICollectionFixture<FixtureDoBanco>
{
    public const string Nome = "PostgreSQL real";
}
