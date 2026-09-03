namespace CentralAntifraude.Domain.Tempo;

/// <summary>
/// Fonte unica de tempo do sistema.
///
/// Decisao congelada na Fase 0 (CLAUDE.md secao 100): nenhum codigo de src/
/// pode chamar DateTime.UtcNow diretamente - o analisador de API proibida
/// (src/BannedSymbols.txt) quebra a compilacao se isso acontecer.
///
/// Motivo concreto: a Central Antifraude tem tres tempos distintos
/// (OccurredAt, ReceivedAt, EvaluatedAt) e regras de velocidade que dependem
/// de janelas temporais. Testar isso exige controlar o relogio.
/// </summary>
public interface IRelogio
{
    /// <summary>
    /// Instante atual, sempre em UTC (offset zero).
    /// Implementacoes devem garantir <c>Agora.Offset == TimeSpan.Zero</c>.
    /// </summary>
    DateTimeOffset Agora { get; }
}
