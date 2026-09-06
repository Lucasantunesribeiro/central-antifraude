using System.Globalization;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.Application.Alertas;

/// <summary>
/// O que a fila operacional aceita como filtro.
///
/// **Vocabulario fechado, resolvido em um lugar so.** Cada campo aqui vira ou
/// um valor de enum conhecido, ou uma recusa explicita. Nada do que o cliente
/// digitou alcanca a montagem da consulta — a mesma regra que
/// <see cref="ParametrosDeOrdenacao"/> aplica ao campo de ordenacao desde a
/// Fase 0, e pelo mesmo motivo.
///
/// Recusar em vez de ignorar e deliberado. Um filtro desconhecido silenciosamente
/// descartado devolveria a lista inteira e o analista acreditaria estar vendo
/// so os bloqueios — numa fila de fraude, acreditar que se filtrou e pior do
/// que receber um erro.
/// </summary>
public sealed record FiltroDeAlertas
{
    private FiltroDeAlertas(
        Decisao? decisao,
        PrioridadeDeAlerta? prioridade,
        StatusDoAlerta? status,
        int? scoreMinimo,
        DateTimeOffset? de,
        DateTimeOffset? ate)
    {
        Decisao = decisao;
        Prioridade = prioridade;
        Status = status;
        ScoreMinimo = scoreMinimo;
        De = de;
        Ate = ate;
    }

    /// <summary>Nenhum filtro: a fila inteira do tenant.</summary>
    public static FiltroDeAlertas Nenhum { get; } = new(null, null, null, null, null, null);

    public Decisao? Decisao { get; }

    public PrioridadeDeAlerta? Prioridade { get; }

    /// <summary>
    /// Situacao do alerta na operacao.
    ///
    /// Existe desde a Fase 7, quando o alerta passou a ter mais de um estado. E
    /// o filtro que separa **fila de trabalho** de **historico**: sem ele, um
    /// alerta ja investigado ficaria na fila para sempre e a tela deixaria de
    /// dizer o que ainda precisa de gente.
    /// </summary>
    public StatusDoAlerta? Status { get; }

    /// <summary>Score da avaliacao, inclusive.</summary>
    public int? ScoreMinimo { get; }

    /// <summary>Inicio do periodo de criacao do alerta, inclusive.</summary>
    public DateTimeOffset? De { get; }

    /// <summary>Fim do periodo de criacao do alerta, inclusive.</summary>
    public DateTimeOffset? Ate { get; }

    public bool EstaVazio =>
        Decisao is null && Prioridade is null && Status is null &&
        ScoreMinimo is null && De is null && Ate is null;

    public static bool TentarCriar(
        string? decisao,
        string? prioridade,
        string? status,
        int? scoreMinimo,
        DateTimeOffset? de,
        DateTimeOffset? ate,
        out FiltroDeAlertas filtro,
        out string erro)
    {
        filtro = Nenhum;

        if (!VocabularioFechado.TentarResolver<Decisao>(decisao, "decisao", out var decisaoResolvida, out erro))
        {
            return false;
        }

        if (!VocabularioFechado.TentarResolver<PrioridadeDeAlerta>(
                prioridade,
                "prioridade",
                out var prioridadeResolvida,
                out erro))
        {
            return false;
        }

        if (!VocabularioFechado.TentarResolver<StatusDoAlerta>(
                status,
                "status",
                out var statusResolvido,
                out erro))
        {
            return false;
        }

        if (scoreMinimo is < VersaoDePerfilDeRisco.ScoreMinimo or > VersaoDePerfilDeRisco.ScoreMaximo)
        {
            erro = string.Create(
                CultureInfo.InvariantCulture,
                $"O score minimo deve estar entre {VersaoDePerfilDeRisco.ScoreMinimo} e {VersaoDePerfilDeRisco.ScoreMaximo}.");

            return false;
        }

        if (de is not null && ate is not null && de > ate)
        {
            erro = "O inicio do periodo nao pode ser depois do fim.";
            return false;
        }

        filtro = new FiltroDeAlertas(
            decisaoResolvida,
            prioridadeResolvida,
            statusResolvido,
            scoreMinimo,
            de,
            ate);
        erro = string.Empty;

        return true;
    }
}

/// <summary>Um alerta com os sinais da avaliacao que o originou.</summary>
public sealed record AlertaNaFila(Alerta Alerta, IReadOnlyList<SinalDeRisco> Sinais);

/// <summary>Leitura da fila operacional de alertas.</summary>
public interface IRepositorioDeAlertas
{
    /// <summary>
    /// Pagina da fila, ja filtrada pelo tenant atual pelo filtro global.
    ///
    /// Devolve so os alertas; os sinais sao carregados em lote pelo servico,
    /// em uma consulta para a pagina inteira.
    /// </summary>
    Task<Pagina<Alerta>> ListarAsync(
        FiltroDeAlertas filtro,
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken);

    /// <summary>Um alerta do tenant atual. Nulo para id de outro tenant.</summary>
    Task<Alerta?> BuscarPorIdAsync(Guid alertaId, CancellationToken cancellationToken);

    /// <summary>
    /// Varios alertas do tenant atual, para abrir um caso de uma vez so.
    ///
    /// Devolve somente os que existem no tenant. Quem chamou compara a
    /// quantidade: um identificador que nao volta pode ser inexistente ou de
    /// outra organizacao, e os dois casos precisam ser indistinguiveis.
    /// </summary>
    Task<IReadOnlyList<Alerta>> BuscarPorIdsAsync(
        IReadOnlyCollection<Guid> alertasIds,
        CancellationToken cancellationToken);

    void Adicionar(Alerta alerta);
}
