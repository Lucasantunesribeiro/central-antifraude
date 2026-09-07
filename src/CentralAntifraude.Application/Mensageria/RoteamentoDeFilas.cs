using CentralAntifraude.Domain.Eventos;

namespace CentralAntifraude.Application.Mensageria;

/// <summary>
/// Qual fila recebe cada tipo de evento.
///
/// **Duas filas, e nao uma** (CLAUDE.md secao 30). Backtest e trabalho longo e
/// em lote; alerta e trabalho curto e operacional. Na mesma fila, uma execucao
/// de cinco mil transacoes ficaria na frente do alerta de uma transacao
/// bloqueada — e o analista descobriria isso pela demora, sem nada no sistema
/// explicando por que.
///
/// **Lista fechada, com padrao operacional.** Um tipo novo cai na fila
/// operacional ate que alguem decida o contrario aqui. E a escolha mais
/// conservadora: a fila operacional tem consumidor, e a de backtests recusa o
/// que nao entende.
/// </summary>
public static class RoteamentoDeFilas
{
    public static string Para(string tipoComposto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tipoComposto);

        return tipoComposto switch
        {
            BacktestSolicitadoV1.NomeDoTipo => IFilaDeMensagens.FilaDeBacktests,
            _ => IFilaDeMensagens.FilaOperacional,
        };
    }
}
