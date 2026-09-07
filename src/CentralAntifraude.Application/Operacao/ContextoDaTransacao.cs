using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Investigacao;

namespace CentralAntifraude.Application.Operacao;

/// <summary>
/// Um alerta gerado por esta transacao, com o caso que o recolheu quando ele
/// existe.
///
/// O caso vem junto porque a pergunta de quem abre uma transacao nunca para no
/// alerta: e "alguem ja olhou isto?". Uma tela que mostrasse o alerta sem
/// dizer se ele virou caso obrigaria o analista a procurar na outra aba.
/// </summary>
public sealed record AlertaDaTransacao(
    Guid AlertaId,
    PrioridadeDeAlerta Prioridade,
    StatusDoAlerta Status,
    DateTimeOffset CriadoEm,
    Guid? CasoId,
    string? TituloDoCaso,
    StatusDoCaso? StatusDoCaso);

/// <summary>
/// O que aconteceu com uma transacao **depois** da avaliacao.
///
/// A avaliacao responde "por que esta decisao"; isto responde "e o que a
/// operacao fez a respeito". Sao coisas diferentes, e o ROADMAP 10.3 pede as
/// duas na mesma tela.
///
/// <see cref="Veredito"/> e a conclusao humana da Fase 7. Ela pode contradizer
/// a decisao do motor — decisao `Revisar`, veredito `Legitima` — e isso e um
/// falso positivo legitimo, nao um defeito (CLAUDE.md secao 11).
/// </summary>
public sealed record ContextoOperacionalDaTransacao(
    IReadOnlyList<AlertaDaTransacao> Alertas,
    ResultadoDaInvestigacao? Veredito,
    Guid? CasoDoVeredito,
    DateTimeOffset? VereditoRegistradoEm)
{
    public static ContextoOperacionalDaTransacao Vazio { get; } = new([], null, null, null);
}
