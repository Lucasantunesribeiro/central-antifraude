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
        int? scoreMinimo,
        DateTimeOffset? de,
        DateTimeOffset? ate)
    {
        Decisao = decisao;
        Prioridade = prioridade;
        ScoreMinimo = scoreMinimo;
        De = de;
        Ate = ate;
    }

    /// <summary>Nenhum filtro: a fila inteira do tenant.</summary>
    public static FiltroDeAlertas Nenhum { get; } = new(null, null, null, null, null);

    public Decisao? Decisao { get; }

    public PrioridadeDeAlerta? Prioridade { get; }

    /// <summary>Score da avaliacao, inclusive.</summary>
    public int? ScoreMinimo { get; }

    /// <summary>Inicio do periodo de criacao do alerta, inclusive.</summary>
    public DateTimeOffset? De { get; }

    /// <summary>Fim do periodo de criacao do alerta, inclusive.</summary>
    public DateTimeOffset? Ate { get; }

    public bool EstaVazio =>
        Decisao is null && Prioridade is null && ScoreMinimo is null && De is null && Ate is null;

    public static bool TentarCriar(
        string? decisao,
        string? prioridade,
        int? scoreMinimo,
        DateTimeOffset? de,
        DateTimeOffset? ate,
        out FiltroDeAlertas filtro,
        out string erro)
    {
        filtro = Nenhum;

        if (!TentarResolver<Decisao>(decisao, "decisao", out var decisaoResolvida, out erro))
        {
            return false;
        }

        if (!TentarResolver<PrioridadeDeAlerta>(
                prioridade,
                "prioridade",
                out var prioridadeResolvida,
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

        filtro = new FiltroDeAlertas(decisaoResolvida, prioridadeResolvida, scoreMinimo, de, ate);
        erro = string.Empty;

        return true;
    }

    /// <summary>
    /// Resolve um texto contra um enum fechado, comparando com os NOMES.
    ///
    /// <c>Enum.TryParse</c> sozinho nao serve como porta de entrada: ele
    /// tambem aceita o numero subjacente, entao <c>?decisao=2</c> viraria
    /// <c>Revisar</c> e <c>?prioridade=99</c> atravessaria como um valor que
    /// nao existe no enum. Comparar com a lista de nomes antes de converter
    /// fecha os dois buracos e deixa o vocabulario aceito explicito.
    /// </summary>
    private static bool TentarResolver<T>(string? texto, string nome, out T? valor, out string erro)
        where T : struct, Enum
    {
        valor = null;
        erro = string.Empty;

        if (string.IsNullOrWhiteSpace(texto))
        {
            return true;
        }

        var informado = texto.Trim();

        var canonico = Enum.GetNames<T>().FirstOrDefault(
            aceito => string.Equals(aceito, informado, StringComparison.OrdinalIgnoreCase));

        if (canonico is not null)
        {
            valor = Enum.Parse<T>(canonico);
            return true;
        }

        // Os nomes aceitos vao na mensagem de proposito: sao contrato publico,
        // e nao ha o que vazar em dizer que existem tres decisoes.
        erro = $"Valor invalido para '{nome}'. Aceitos: {string.Join(", ", Enum.GetNames<T>())}.";

        return false;
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

    void Adicionar(Alerta alerta);
}
