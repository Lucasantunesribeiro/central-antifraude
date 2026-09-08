using System.Globalization;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// O que o PostgreSQL realmente faz com as consultas criticas. (ROADMAP 12.8)
///
/// **Por que ler o plano, e nao cronometrar.** Um cronometro em maquina de
/// desenvolvimento mede o cache, a carga do momento e a sorte. O plano de
/// execucao responde a pergunta que interessa e nao depende de nada disso:
/// **este indice esta sendo usado?** Uma consulta que varre a tabela inteira em
/// 4 ms hoje varre em 4 s no ano que vem, e o cronometro so avisa no ano que
/// vem.
///
/// **E aqui esta a explicacao do numero mais estranho da Fase 12.** O cenario A
/// de carga mostra clientes INDEPENDENTES entrando em contencao de
/// serializacao, o que nao deveria acontecer: eles nao compartilham dado
/// nenhum. A causa esta neste arquivo. Sob `SERIALIZABLE`, o PostgreSQL toma
/// predicado sobre o que a consulta LE — e uma varredura sequencial le a
/// relacao inteira, entao o predicado cobre a tabela toda e todos passam a
/// conflitar com todos. Com volume, o planejador escolhe o indice, o predicado
/// encolhe para as linhas daquele cliente e a contencao artificial some.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class DesempenhoDeConsultasTests : IAsyncLifetime
{
    /// <summary>
    /// Volume semeado antes de olhar o plano.
    ///
    /// Nao e "muito dado": e dado suficiente para o planejador ter escolha. Com
    /// dezenas de linhas, varrer tudo e legitimamente mais barato do que abrir
    /// um indice, e um teste que exigisse indice ali estaria exigindo a decisao
    /// errada do banco.
    /// </summary>
    private const int VolumeSemeado = 4_000;

    private readonly FixtureDoBanco _banco;
    private TenantDeTeste _tenant = null!;
    private Guid _integracaoId;

    public DesempenhoDeConsultasTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"plano-{Guid.NewGuid():N}"[..14],
            Cancelamento);

        await using var fabrica = new FabricaDaApi(_banco.StringDeConexao);
        using var cliente = fabrica.CriarClienteSemCookieAutomatico();

        var integracao = await CenarioDeIngestao.CriarIntegracaoAsync(cliente, _tenant, Cancelamento);
        _integracaoId = integracao.Id;

        await SemearAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_janela_historica_do_cliente_usa_indice_e_nao_varre_a_tabela()
    {
        // Espelha `ProvedorDeContextoDeRisco.CarregarAsync`: tenant, cliente,
        // janela de tempo, ordenacao decrescente e teto. E a consulta do
        // caminho critico — ela roda em TODA avaliacao, dentro da transacao
        // SERIALIZABLE.
        var plano = await ExplicarAsync(
            """
            SELECT id, valor, ocorrida_em
            FROM transacoes
            WHERE organizacao_id = @org
              AND cliente_externo_id = @cliente
              AND ocorrida_em >= now() - interval '90 days'
            ORDER BY ocorrida_em DESC
            LIMIT 200
            """,
            ("cliente", "cli-plano-7"));

        Assert.Contains("Index", plano, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan on transacoes", plano, StringComparison.Ordinal);

        // O indice e o da Fase 2, e a ORDEM das colunas e o que o faz servir:
        // organizacao, cliente e so entao tempo. Invertida, ele responderia
        // "todas as transacoes de setembro" — que ninguem pergunta — e nao
        // "as ultimas deste cliente", que e a unica pergunta do motor.
        Assert.Contains(
            "ix_transacoes_organizacao_id_cliente_externo_id_ocorrida_em",
            plano,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_listagem_do_console_usa_o_indice_de_tempo_da_organizacao()
    {
        var plano = await ExplicarAsync(
            """
            SELECT id, ocorrida_em
            FROM transacoes
            WHERE organizacao_id = @org
            ORDER BY ocorrida_em DESC
            LIMIT 25
            """);

        // A primeira pagina do console e uma consulta ordenada por tempo sobre
        // a organizacao inteira. Sem indice, o banco ordenaria tudo para
        // devolver 25 linhas — e o custo cresceria com o historico do cliente,
        // que e justamente o que nunca para de crescer.
        Assert.Contains("Index", plano, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan on transacoes", plano, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_leitura_da_outbox_pendente_usa_o_indice_parcial()
    {
        var plano = await ExplicarAsync(
            """
            SELECT id
            FROM eventos_de_saida
            WHERE publicado_em IS NULL
            ORDER BY ocorrido_em
            LIMIT 20
            """);

        // Indice PARCIAL, e nao completo. A Outbox e uma tabela que so cresce:
        // milhoes de linhas publicadas e algumas dezenas pendentes. Um indice
        // sobre tudo indexaria justamente as linhas que ninguem procura, e
        // ficaria maior que a resposta.
        Assert.Contains("ix_eventos_de_saida_pendentes", plano, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Todo_indice_declarado_no_modelo_existe_no_banco()
    {
        // Uma migration pode ser escrita sem o indice que o mapeamento declara,
        // e nada quebra: as consultas continuam corretas, so ficam lentas. E o
        // tipo de defeito que nao aparece em teste nenhum ate virar incidente.
        var esperados = new[]
        {
            "ix_transacoes_organizacao_id_cliente_externo_id_ocorrida_em",
            "ix_transacoes_organizacao_id_ocorrida_em",
            "ix_transacoes_organizacao_id_recebida_em",
            "ix_eventos_de_saida_pendentes",
            "ix_alertas_organizacao_status_criado",
        };

        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);
        var conexao = (NpgsqlConnection)contexto.Database.GetDbConnection();
        await conexao.OpenAsync(Cancelamento);

        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SELECT indexname FROM pg_indexes WHERE schemaname = 'public'";

        var existentes = new List<string>();
        await using (var leitor = await comando.ExecuteReaderAsync(Cancelamento))
        {
            while (await leitor.ReadAsync(Cancelamento))
            {
                existentes.Add(leitor.GetString(0));
            }
        }

        Assert.All(esperados, nome => Assert.Contains(nome, existentes));
    }

    // -----------------------------------------------------------------------
    // Auxiliares
    // -----------------------------------------------------------------------

    /// <summary>
    /// Insere volume direto no banco, sem passar pela API.
    ///
    /// Quatro mil requisicoes HTTP levariam minutos e nao provariam nada a mais:
    /// o que este teste observa e o PLANEJADOR, e para ele so importa quantas
    /// linhas existem e como elas se distribuem. O <c>ANALYZE</c> ao final nao e
    /// opcional — sem estatistica atualizada o planejador decide pelo palpite
    /// padrao, e o teste mediria esse palpite.
    /// </summary>
    private async Task SemearAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);
        var conexao = (NpgsqlConnection)contexto.Database.GetDbConnection();
        await conexao.OpenAsync(Cancelamento);

        await using (var comando = conexao.CreateCommand())
        {
            comando.CommandText =
                """
                INSERT INTO transacoes (
                    id, organizacao_id, integracao_id, identificador_externo,
                    ocorrida_em, recebida_em, cliente_externo_id,
                    referencia_do_instrumento, fingerprint_do_dispositivo,
                    fingerprint_do_ip, pais_de_origem, chave_de_idempotencia,
                    fingerprint_do_payload, moeda, valor, id_de_correlacao)
                SELECT
                    gen_random_uuid(), @org, @integracao, 'plano-' || i,
                    now() - (i || ' minutes')::interval, now(), 'cli-plano-' || (i % 40),
                    'pi_plano', 'disp-plano', NULL, 'BR', 'idem-plano-' || i || '-' || @sufixo,
                    'fp-plano', 'BRL', 100.0000, NULL
                FROM generate_series(1, @quantidade) AS i
                """;

            comando.Parameters.Add(new NpgsqlParameter("org", NpgsqlDbType.Uuid) { Value = _tenant.OrganizacaoId });
            comando.Parameters.Add(new NpgsqlParameter("integracao", NpgsqlDbType.Uuid) { Value = _integracaoId });
            comando.Parameters.Add(new NpgsqlParameter("quantidade", NpgsqlDbType.Integer) { Value = VolumeSemeado });
            comando.Parameters.Add(new NpgsqlParameter("sufixo", NpgsqlDbType.Text)
            {
                Value = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            });

            await comando.ExecuteNonQueryAsync(Cancelamento);
        }

        // A Outbox tambem precisa de volume, e por um motivo especifico: o
        // indice dela e PARCIAL — so cobre o que esta pendente. Um teste sobre
        // a tabela vazia mediria a decisao do planejador para zero linha, que e
        // sempre varrer, e concluiria erradamente que o indice nao serve.
        await using (var outbox = conexao.CreateCommand())
        {
            outbox.CommandText =
                """
                INSERT INTO eventos_de_saida (
                    id, organizacao_id, tipo, ocorrido_em, id_de_correlacao,
                    conteudo, publicado_em, tentativas_de_publicacao)
                SELECT
                    gen_random_uuid(), @org, 'PlanoDeTeste.v1',
                    now() - (i || ' seconds')::interval, 'plano-' || i,
                    '{}'::jsonb,
                    CASE WHEN i % 50 = 0 THEN NULL ELSE now() END,
                    0
                FROM generate_series(1, @quantidade) AS i
                """;

            outbox.Parameters.Add(new NpgsqlParameter("org", NpgsqlDbType.Uuid) { Value = _tenant.OrganizacaoId });
            outbox.Parameters.Add(new NpgsqlParameter("quantidade", NpgsqlDbType.Integer) { Value = VolumeSemeado });

            await outbox.ExecuteNonQueryAsync(Cancelamento);
        }

        await using var estatisticas = conexao.CreateCommand();
        estatisticas.CommandText = "ANALYZE transacoes; ANALYZE eventos_de_saida; ANALYZE alertas;";
        await estatisticas.ExecuteNonQueryAsync(Cancelamento);
    }

    /// <summary>Devolve o plano em texto, como o banco o produziu.</summary>
    private async Task<string> ExplicarAsync(
        string consulta,
        params (string Nome, string Valor)[] parametros)
    {
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);
        var conexao = (NpgsqlConnection)contexto.Database.GetDbConnection();
        await conexao.OpenAsync(Cancelamento);

        await using var comando = conexao.CreateCommand();
        comando.CommandText = "EXPLAIN (FORMAT TEXT) " + consulta;
        comando.Parameters.Add(new NpgsqlParameter("org", NpgsqlDbType.Uuid) { Value = _tenant.OrganizacaoId });

        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter(nome, NpgsqlDbType.Text) { Value = valor });
        }

        var linhas = new List<string>();

        await using (var leitor = await comando.ExecuteReaderAsync(Cancelamento))
        {
            while (await leitor.ReadAsync(Cancelamento))
            {
                linhas.Add(leitor.GetString(0));
            }
        }

        var plano = string.Join('\n', linhas);

        TestContext.Current.TestOutputHelper?.WriteLine(plano);

        return plano;
    }
}
