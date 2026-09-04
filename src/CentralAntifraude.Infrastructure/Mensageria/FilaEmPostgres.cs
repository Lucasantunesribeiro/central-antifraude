using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace CentralAntifraude.Infrastructure.Mensageria;

/// <summary>
/// A fila operacional, implementada sobre o PostgreSQL que ja existe.
///
/// **Por que nao a SQS de verdade agora.** O ROADMAP secao 5.5 e explicito:
/// "infra cloud real pode continuar simulada/local ate a Fase 14". Criar fila
/// remota nesta fase seria recurso de nuvem sem autorizacao (CLAUDE.md secoes
/// 107.5 e 109), e um SDK que so seria exercitado de verdade daqui a nove
/// fases e codigo morto com aparencia de arquitetura.
///
/// **Por que nao uma fila em memoria.** Porque esconderia justamente o que
/// precisa ser provado. Uma lista em memoria entrega uma vez, em ordem, e sem
/// visibilidade — as tres garantias que o SQS Standard NAO da. Os testes
/// passariam e o defeito apareceria em producao (CLAUDE.md secao 31).
///
/// **O que esta implementacao reproduz do SQS Standard:**
///
/// | Comportamento | Aqui | No SQS |
/// |---|---|---|
/// | Entrega ao menos uma vez | receber esconde, apagar remove | idem |
/// | Sem ordem garantida | `SKIP LOCKED` entrega o que estiver livre | idem |
/// | Visibilidade | `disponivel_em` no futuro | `VisibilityTimeout` |
/// | Recibo por entrega | `recibo` sorteado a cada recebimento | `ReceiptHandle` |
/// | Contagem de entregas | `recebimentos` | `ApproximateReceiveCount` |
/// | Redrive para DLQ | move ao passar do maximo | `maxReceiveCount` |
///
/// A Fase 14 troca esta classe por um adaptador do SDK. O contrato nao muda —
/// e o conjunto de testes de contrato existe justamente para verificar isso.
/// </summary>
public sealed class FilaEmPostgres : IFilaDeMensagens
{
    private readonly CentralAntifraudeDbContext _contexto;
    private readonly OpcoesDaFila _opcoes;

    public FilaEmPostgres(CentralAntifraudeDbContext contexto, OpcoesDaFila opcoes)
    {
        _contexto = contexto;
        _opcoes = opcoes;
    }

    public async Task EnviarAsync(string fila, string corpo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fila);
        ArgumentException.ThrowIfNullOrWhiteSpace(corpo);

        await ExecutarAsync(
            """
            INSERT INTO fila_de_mensagens (id, fila, corpo, disponivel_em, recebimentos, inserida_em)
            VALUES (gen_random_uuid(), @fila, @corpo, now(), 0, now())
            """,
            cancellationToken,
            Texto("fila", fila),
            Texto("corpo", corpo));
    }

    /// <summary>
    /// Recebe um lote, escondendo o que entregou.
    ///
    /// O comando faz tudo em uma unica ida ao banco, e isso nao e otimizacao:
    /// selecionar e depois atualizar em passos separados abriria a janela em
    /// que dois workers recebem a mesma mensagem. `FOR UPDATE SKIP LOCKED` e o
    /// que permite varios consumidores sem que um espere pelo outro — quem
    /// chega depois simplesmente pula as linhas ja travadas.
    ///
    /// O recibo e novo a cada entrega. Um recibo antigo, de uma entrega cuja
    /// visibilidade ja expirou, nao apaga a mensagem que outro consumidor
    /// esta processando agora.
    /// </summary>
    public async Task<IReadOnlyList<MensagemRecebida>> ReceberAsync(
        string fila,
        int quantidade,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fila);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantidade);

        // Antes de entregar, tira de circulacao o que ja passou do limite de
        // recebimentos. E o redrive: uma mensagem que sempre falha nao pode
        // voltar para sempre, consumindo o worker e escondendo as boas atras
        // dela.
        await MoverParaMortasAsync(fila, cancellationToken);

        var conexao = (NpgsqlConnection)_contexto.Database.GetDbConnection();
        await AbrirSeNecessarioAsync(conexao, cancellationToken);

        await using var comando = conexao.CreateCommand();
        comando.CommandText =
            """
            UPDATE fila_de_mensagens
            SET disponivel_em = now() + make_interval(secs => @visibilidade),
                recebimentos = recebimentos + 1,
                recibo = gen_random_uuid()
            WHERE id IN (
                SELECT id
                FROM fila_de_mensagens
                WHERE fila = @fila AND disponivel_em <= now()
                ORDER BY disponivel_em
                FOR UPDATE SKIP LOCKED
                LIMIT @quantidade)
            RETURNING recibo, corpo, recebimentos, inserida_em
            """;

        comando.Transaction = TransacaoAtual();
        comando.Parameters.Add(Texto("fila", fila));
        comando.Parameters.Add(new NpgsqlParameter("visibilidade", NpgsqlDbType.Double)
        {
            Value = _opcoes.Visibilidade.TotalSeconds,
        });
        comando.Parameters.Add(new NpgsqlParameter("quantidade", NpgsqlDbType.Integer)
        {
            Value = quantidade,
        });

        var mensagens = new List<MensagemRecebida>();

        await using var leitor = await comando.ExecuteReaderAsync(cancellationToken);

        while (await leitor.ReadAsync(cancellationToken))
        {
            mensagens.Add(new MensagemRecebida(
                leitor.GetGuid(0).ToString(),
                leitor.GetString(1),
                leitor.GetInt32(2),
                leitor.GetFieldValue<DateTimeOffset>(3)));
        }

        return mensagens;
    }

    public Task ApagarAsync(string recibo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recibo);

        return ExecutarAsync(
            "DELETE FROM fila_de_mensagens WHERE recibo = @recibo",
            cancellationToken,
            Uuid("recibo", recibo));
    }

    public Task DevolverAsync(string recibo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recibo);

        // O recibo e invalidado junto: a entrega terminou, e um recibo que
        // continuasse valido poderia apagar a proxima entrega da mesma
        // mensagem.
        return ExecutarAsync(
            "UPDATE fila_de_mensagens SET disponivel_em = now(), recibo = NULL WHERE recibo = @recibo",
            cancellationToken,
            Uuid("recibo", recibo));
    }

    public async Task<int> ContarPendentesAsync(string fila, CancellationToken cancellationToken) =>
        await ContarAsync(
            "SELECT count(*)::int FROM fila_de_mensagens WHERE fila = @fila",
            fila,
            cancellationToken);

    public async Task<int> ContarMortasAsync(string fila, CancellationToken cancellationToken) =>
        await ContarAsync(
            "SELECT count(*)::int FROM mensagens_mortas WHERE fila = @fila",
            fila,
            cancellationToken);

    /// <summary>
    /// Move para a fila de mortas o que ja foi entregue vezes demais.
    ///
    /// A checagem acontece no recebimento, e nao no descarte, pelo mesmo
    /// motivo do SQS: so na hora de entregar de novo e que se sabe que a
    /// entrega anterior nao foi confirmada.
    /// </summary>
    private Task MoverParaMortasAsync(string fila, CancellationToken cancellationToken) =>
        ExecutarAsync(
            """
            WITH mortas AS (
                DELETE FROM fila_de_mensagens
                WHERE fila = @fila
                  AND disponivel_em <= now()
                  AND recebimentos >= @maximo
                RETURNING id, fila, corpo, recebimentos, inserida_em)
            INSERT INTO mensagens_mortas (id, fila, corpo, recebimentos, inserida_em, movida_em, motivo)
            SELECT id, fila, corpo, recebimentos, inserida_em, now(), @motivo
            FROM mortas
            """,
            cancellationToken,
            Texto("fila", fila),
            new NpgsqlParameter("maximo", NpgsqlDbType.Integer) { Value = _opcoes.MaximoDeRecebimentos },
            Texto(
                "motivo",
                $"Excedeu {_opcoes.MaximoDeRecebimentos} recebimentos sem confirmacao."));

    private async Task<int> ContarAsync(string sql, string fila, CancellationToken cancellationToken)
    {
        var conexao = (NpgsqlConnection)_contexto.Database.GetDbConnection();
        await AbrirSeNecessarioAsync(conexao, cancellationToken);

        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        comando.Transaction = TransacaoAtual();
        comando.Parameters.Add(Texto("fila", fila));

        return (int)(await comando.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private async Task ExecutarAsync(
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parametros)
    {
        var conexao = (NpgsqlConnection)_contexto.Database.GetDbConnection();
        await AbrirSeNecessarioAsync(conexao, cancellationToken);

        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        comando.Transaction = TransacaoAtual();
        comando.Parameters.AddRange(parametros);

        await comando.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Enlista o comando na transacao aberta pelo EF, quando houver.
    ///
    /// Sem isto, o envio da mensagem ficaria FORA da transacao do despachante:
    /// a mensagem sairia mesmo que o commit falhasse depois, e o sistema
    /// avisaria sobre algo que nao aconteceu.
    /// </summary>
    private NpgsqlTransaction? TransacaoAtual() =>
        _contexto.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;

    private static async Task AbrirSeNecessarioAsync(
        NpgsqlConnection conexao,
        CancellationToken cancellationToken)
    {
        if (conexao.State != System.Data.ConnectionState.Open)
        {
            await conexao.OpenAsync(cancellationToken);
        }
    }

    private static NpgsqlParameter Texto(string nome, string valor) =>
        new(nome, NpgsqlDbType.Text) { Value = valor };

    private static NpgsqlParameter Uuid(string nome, string valor) =>
        new(nome, NpgsqlDbType.Uuid)
        {
            Value = Guid.TryParse(valor, out var guid) ? guid : Guid.Empty,
        };
}
