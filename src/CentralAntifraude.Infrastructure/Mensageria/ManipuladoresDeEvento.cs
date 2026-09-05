using System.Diagnostics.Metrics;
using CentralAntifraude.Application.Alertas;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Tempo;
using CentralAntifraude.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace CentralAntifraude.Infrastructure.Mensageria;

/// <summary>
/// Efeito 1: soma um na contagem do dia.
///
/// Nasceu na Fase 5 como o unico efeito do backbone, e continua exatamente
/// como estava. **Um contador e o pior caso para entrega duplicada** — somar
/// duas vezes nao deixa rastro nenhum — e e por isso que ele foi escolhido:
/// se a Inbox falhar, o numero mente e o teste denuncia.
///
/// A Fase 10 usa esta tabela no painel operacional.
/// </summary>
public sealed class ProjecaoDeDecisoesDiarias : IManipuladorDeEvento
{
    /// <summary>Nome deste consumidor na Inbox. Nunca renomear.</summary>
    public const string NomeDoConsumidor = "resumo-diario-de-decisoes";

    private readonly CentralAntifraudeDbContext _contexto;

    public ProjecaoDeDecisoesDiarias(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    public string Consumidor => NomeDoConsumidor;

    /// <summary>
    /// <c>INSERT ... ON CONFLICT DO UPDATE</c> porque a linha pode nao existir
    /// ainda e dois workers podem cria-la ao mesmo tempo. Ler, somar e gravar
    /// em passos separados perderia incrementos sob concorrencia.
    ///
    /// O dia e o da AVALIACAO, em UTC: uma transacao atrasada de tres dias
    /// atras foi decidida hoje, e e hoje que ela entrou na fila do analista.
    /// </summary>
    public async Task AplicarAsync(EnvelopeDeEvento envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Conteudo is not TransacaoAvaliadaV1 conteudo)
        {
            return;
        }

        var conexao = (NpgsqlConnection)_contexto.Database.GetDbConnection();

        await using var comando = conexao.CreateCommand();
        comando.Transaction =
            _contexto.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;

        comando.CommandText =
            """
            INSERT INTO resumo_diario_de_decisoes
                (id, organizacao_id, dia, decisao, quantidade, atualizado_em)
            VALUES (gen_random_uuid(), @organizacao, @dia, @decisao, 1, now())
            ON CONFLICT (organizacao_id, dia, decisao)
            DO UPDATE SET quantidade = resumo_diario_de_decisoes.quantidade + 1,
                          atualizado_em = now()
            """;

        comando.Parameters.Add(new NpgsqlParameter("organizacao", NpgsqlDbType.Uuid)
        {
            Value = envelope.TenantId,
        });
        comando.Parameters.Add(new NpgsqlParameter("dia", NpgsqlDbType.Date)
        {
            Value = DateOnly.FromDateTime(conteudo.AvaliadaEm.UtcDateTime),
        });
        comando.Parameters.Add(new NpgsqlParameter("decisao", NpgsqlDbType.Text)
        {
            Value = conteudo.Decisao.ToString(),
        });

        await comando.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// Efeito 2: transforma a avaliacao em trabalho para o analista.
///
/// **Este manipulador nao decide risco.** Ele le a decisao que o motor ja
/// tomou e pergunta a <see cref="PoliticaDeAlertas"/> se aquilo merece fila.
/// Reavaliar aqui produziria um resultado potencialmente diferente do que o
/// integrador recebeu na resposta sincrona — o painel discordaria do recibo.
///
/// **Duas camadas de idempotencia, e as duas fazem falta.** A Inbox impede o
/// segundo processamento do mesmo evento; a restricao unica de
/// <c>avaliacao_id</c> impede o segundo alerta da mesma avaliacao, venha ele
/// de onde vier. O `CLAUDE.md` secao 42 pede exatamente isso, e aqui o dominio
/// justifica: alerta duplicado nao e numero errado num painel, e um analista
/// investigando duas vezes o mesmo caso.
/// </summary>
public sealed partial class CriadorDeAlertas : IManipuladorDeEvento
{
    /// <summary>Nome deste consumidor na Inbox. Nunca renomear.</summary>
    public const string NomeDoConsumidor = "criador-de-alertas";

    /// <summary>Nome do medidor, para quem for coletar metricas na Fase 12.</summary>
    public const string NomeDoMedidor = "CentralAntifraude.Alertas";

    private static readonly Meter Medidor = new(NomeDoMedidor);

    /// <summary>
    /// Alertas criados, por prioridade.
    ///
    /// A prioridade tem dois valores possiveis, entao serve como dimensao. Nem
    /// tenant, nem transacao, nem correlacao entram: seriam dimensoes de alta
    /// cardinalidade, e o `CLAUDE.md` secao 71 proibe exatamente isso.
    /// </summary>
    private static readonly Counter<long> Criados =
        Medidor.CreateCounter<long>("alertas_criados");

    private readonly IRepositorioDeAlertas _alertas;
    private readonly IRelogio _relogio;
    private readonly ILogger<CriadorDeAlertas> _log;

    public CriadorDeAlertas(
        IRepositorioDeAlertas alertas,
        IRelogio relogio,
        ILogger<CriadorDeAlertas> log)
    {
        _alertas = alertas;
        _relogio = relogio;
        _log = log;
    }

    public string Consumidor => NomeDoConsumidor;

    public Task AplicarAsync(EnvelopeDeEvento envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Conteudo is not TransacaoAvaliadaV1 conteudo)
        {
            return Task.CompletedTask;
        }

        var alerta = PoliticaDeAlertas.Avaliar(
            envelope.TenantId,
            conteudo,
            envelope.EventId,
            envelope.CorrelationId,
            _relogio.Agora);

        if (alerta is null)
        {
            // `Permitir` nao gera alerta. Nao e falha, e nao vira log de aviso:
            // e o desfecho esperado da maioria absoluta das transacoes.
            return Task.CompletedTask;
        }

        // Adiciona sem gravar. Quem grava e o worker, no mesmo `SaveChanges`
        // que registra a Inbox — separar os dois abriria o instante em que o
        // alerta existe e a marca de processado nao.
        _alertas.Adicionar(alerta);

        Criados.Add(1, new KeyValuePair<string, object?>("prioridade", alerta.Prioridade.ToString()));

        // Score e cliente ficam de fora do log: sao dado do tenant
        // (`CLAUDE.md` secao 70). O identificador do alerta e a correlacao
        // bastam para achar tudo o mais dentro do banco, com autorizacao.
        var prioridade = alerta.Prioridade.ToString();
        RegistrarAlerta(_log, alerta.Id, prioridade, envelope.CorrelationId);

        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 610,
        Level = LogLevel.Information,
        Message = "Alerta {AlertaId} criado com prioridade {Prioridade}. Correlacao {IdDeCorrelacao}.")]
    private static partial void RegistrarAlerta(
        ILogger logger,
        Guid alertaId,
        string prioridade,
        string idDeCorrelacao);
}
