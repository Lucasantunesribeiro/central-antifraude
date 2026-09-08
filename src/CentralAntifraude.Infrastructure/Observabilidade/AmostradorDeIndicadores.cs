using System.Diagnostics;
using System.Diagnostics.Metrics;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Application.Observabilidade;
using CentralAntifraude.Infrastructure.Mensageria;
using CentralAntifraude.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CentralAntifraude.Infrastructure.Observabilidade;

/// <summary>
/// Mede o que e estado, e nao evento: profundidade de fila e idade do atraso.
///
/// **Por que separado dos contadores.** Um contador sobe quando algo acontece,
/// e o proprio codigo que faz a coisa o incrementa de graca. Profundidade e
/// diferente: ninguem "faz" uma fila ter 400 mensagens; e preciso ir perguntar
/// ao banco. Isso custa consulta, e uma metrica que custa consulta nao pode ser
/// coletada no ritmo dos ciclos de fundo — os lacos rodam a cada 100 ms quando
/// ha trabalho, e quatro contagens por ciclo seriam dezenas de consultas por
/// segundo para responder uma pergunta que muda devagar (CLAUDE.md secao 78).
///
/// Por isso a amostra e limitada por tempo, e nao por ciclo.
///
/// **A metrica que mais importa aqui e a idade, e nao a contagem.** Uma Outbox
/// com 300 eventos pendentes pode ser um pico normal de trafego; uma Outbox com
/// 3 eventos pendentes ha quarenta minutos e um despachante parado. A contagem
/// sozinha nao distingue os dois casos, e o segundo e o que faz o analista
/// esperar por um alerta que nunca chega.
/// </summary>
public sealed partial class AmostradorDeIndicadores
{
    private static readonly Meter Medidor = new(Telemetria.MedidorDeMensageria);

    private static readonly Gauge<long> OutboxPendentes =
        Medidor.CreateGauge<long>(Telemetria.Instrumentos.OutboxPendentes);

    private static readonly Gauge<double> OutboxIdade =
        Medidor.CreateGauge<double>(Telemetria.Instrumentos.OutboxIdade);

    private static readonly Gauge<long> FilaPendentes =
        Medidor.CreateGauge<long>(Telemetria.Instrumentos.FilaPendentes);

    private static readonly Gauge<long> FilaMortas =
        Medidor.CreateGauge<long>(Telemetria.Instrumentos.FilaMortas);

    /// <summary>
    /// Instante da ultima amostra, compartilhado por todo o processo.
    ///
    /// Estatico porque o amostrador tem escopo de ciclo — uma instancia nova a
    /// cada volta do laco — e um campo de instancia esqueceria a amostra
    /// anterior, tornando o limite de ritmo decorativo.
    /// </summary>
    private static long _ultimaAmostra;

    private readonly CentralAntifraudeDbContext _contexto;
    private readonly IFilaDeMensagens _fila;
    private readonly OpcoesDeSegundoPlano _opcoes;
    private readonly ILogger<AmostradorDeIndicadores> _log;

    public AmostradorDeIndicadores(
        CentralAntifraudeDbContext contexto,
        IFilaDeMensagens fila,
        OpcoesDeSegundoPlano opcoes,
        ILogger<AmostradorDeIndicadores> log)
    {
        _contexto = contexto;
        _fila = fila;
        _opcoes = opcoes;
        _log = log;
    }

    /// <summary>
    /// Amostra, se o intervalo minimo ja passou. Devolve <c>true</c> quando
    /// mediu de fato — o teste usa isso para nao depender de relogio.
    /// </summary>
    public async Task<bool> AmostrarSeVencidoAsync(CancellationToken cancellationToken)
    {
        var agora = Stopwatch.GetTimestamp();
        var anterior = Interlocked.Read(ref _ultimaAmostra);

        if (anterior != 0 &&
            Stopwatch.GetElapsedTime(anterior, agora) <
                TimeSpan.FromMilliseconds(_opcoes.IntervaloDeAmostragemEmMs))
        {
            return false;
        }

        // Reserva a vez antes de medir. Se dois lacos chegarem juntos, apenas
        // um segue — e o outro nao repete as consultas para chegar ao mesmo
        // numero.
        if (Interlocked.CompareExchange(ref _ultimaAmostra, agora, anterior) != anterior)
        {
            return false;
        }

        await AmostrarAsync(cancellationToken);
        return true;
    }

    /// <summary>Mede agora, sem olhar o intervalo.</summary>
    public async Task AmostrarAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (pendentes, idade) = await MedirOutboxAsync(cancellationToken);

            OutboxPendentes.Record(pendentes);
            OutboxIdade.Record(idade);

            string[] filas = [IFilaDeMensagens.FilaOperacional, IFilaDeMensagens.FilaDeBacktests];

            foreach (var fila in filas)
            {
                var etiqueta = new KeyValuePair<string, object?>("fila", fila);

                FilaPendentes.Record(await _fila.ContarPendentesAsync(fila, cancellationToken), etiqueta);
                FilaMortas.Record(await _fila.ContarMortasAsync(fila, cancellationToken), etiqueta);
            }
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            // Medir e importante; medir NAO e critico. Uma falha aqui nao pode
            // derrubar o ciclo que publica eventos — seria trocar a operacao
            // pelo termometro.
            RegistrarFalhaDeAmostragem(_log, excecao);
        }
    }

    /// <summary>
    /// Pendentes e idade do mais antigo, em uma unica ida ao banco.
    ///
    /// Sem filtro de tenant, de proposito: e uma medida do processo, e nao de
    /// uma organizacao. O numero nunca sai por uma rota HTTP — quem o le e o
    /// exportador de metricas, que nao tem tenant e nao devolve dado nenhum a
    /// um cliente do produto.
    /// </summary>
    private async Task<(long Pendentes, double IdadeEmSegundos)> MedirOutboxAsync(
        CancellationToken cancellationToken)
    {
        var conexao = (NpgsqlConnection)_contexto.Database.GetDbConnection();

        if (conexao.State != System.Data.ConnectionState.Open)
        {
            await conexao.OpenAsync(cancellationToken);
        }

        await using var comando = conexao.CreateCommand();
        comando.CommandText =
            """
            SELECT count(*)::bigint,
                   coalesce(extract(epoch FROM now() - min(ocorrido_em)), 0)::float8
            FROM eventos_de_saida
            WHERE publicado_em IS NULL
            """;

        await using var leitor = await comando.ExecuteReaderAsync(cancellationToken);

        return await leitor.ReadAsync(cancellationToken)
            ? (leitor.GetInt64(0), leitor.GetDouble(1))
            : (0, 0);
    }

    [LoggerMessage(
        EventId = 540,
        Level = LogLevel.Warning,
        Message = "Falha ao amostrar indicadores de fila. O ciclo segue normalmente.")]
    private static partial void RegistrarFalhaDeAmostragem(ILogger logger, Exception excecao);
}
