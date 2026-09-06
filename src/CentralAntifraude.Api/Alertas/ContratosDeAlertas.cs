using CentralAntifraude.Application.Alertas;

namespace CentralAntifraude.Api.Alertas;

// ---------------------------------------------------------------------------
// Saida dos alertas.
//
// Nao ha DTO de entrada nesta pasta, e isso e deliberado: nenhuma rota aceita
// alerta, prioridade ou status vindos do cliente. O alerta e produzido pelo
// consumidor a partir de um evento que o proprio sistema publicou, e o unico
// caminho ate o banco passa por `Alerta.Registrar`, que e `internal` ao
// dominio (`CLAUDE.md` secoes 53 e 64).
//
// Criar alerta pela API seria criar trabalho de investigacao a partir de um
// JSON — sem avaliacao por tras, sem sinais e sem explicacao.
// ---------------------------------------------------------------------------

/// <summary>
/// Um sinal na linha da fila: so o que cabe numa varredura de coluna.
///
/// A explicacao e a evidencia ficam de fora de proposito. Elas estao no
/// detalhe da transacao, a um clique, e trazer as frases inteiras de cinquenta
/// alertas transformaria a resposta da tela mais usada do produto em algo
/// dezenas de vezes maior sem que ninguem lesse a maior parte.
/// </summary>
public sealed record SinalResumido(string Tipo, int Pontos);

/// <summary>
/// Um alerta na fila operacional.
///
/// <c>TransacaoId</c> existe para a navegacao exigida pelo ROADMAP 6.6: da
/// fila para a transacao, e de la para a avaliacao e os sinais completos, que
/// a tela da Fase 3 ja mostra.
/// </summary>
public sealed record AlertaResumido(
    Guid Id,
    Guid TransacaoId,
    Guid AvaliacaoId,
    string Decisao,
    int Score,
    string Prioridade,
    string Status,
    Guid? CasoId,
    DateTimeOffset AvaliadaEm,
    DateTimeOffset CriadoEm,
    int VersaoDaPolitica,
    IReadOnlyList<SinalResumido> PrincipaisSinais,
    int TotalDeSinais)
{
    /// <summary>Quantos sinais aparecem resumidos em cada linha da fila.</summary>
    public const int MaximoDeSinaisResumidos = 3;

    public static AlertaResumido De(AlertaNaFila item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var alerta = item.Alerta;

        return new AlertaResumido(
            alerta.Id,
            alerta.TransacaoId,
            alerta.AvaliacaoId,
            alerta.Decisao.ToString(),
            alerta.Score,
            alerta.Prioridade.ToString(),
            alerta.Status.ToString(),
            // A investigacao que levou este alerta, quando existe. E o caminho
            // de volta: da fila para o caso, e nao so do caso para a fila.
            alerta.CasoId,
            alerta.AvaliadaEm,
            alerta.CriadoEm,
            alerta.VersaoDaPolitica,
            // Ja vem na ordem estavel do repositorio: maior peso primeiro.
            [.. item.Sinais
                .Take(MaximoDeSinaisResumidos)
                .Select(s => new SinalResumido(s.Tipo.ToString(), s.Pontos))],
            // O total vai junto para que a tela possa dizer "e mais 2" em vez
            // de deixar o analista achar que a transacao acionou tres regras.
            item.Sinais.Count);
    }
}
