using System.Globalization;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.Application.Transacoes;

/// <summary>
/// O que o console de transacoes aceita como filtro.
///
/// **Vocabulario fechado, resolvido em um lugar so** — a mesma disciplina de
/// <see cref="Alertas.FiltroDeAlertas"/> e de <see cref="ParametrosDeOrdenacao"/>.
/// Nada do que o cliente digitou alcanca a montagem da consulta: decisao e tipo
/// de regra viram valores de enum conhecidos ou uma recusa explicita, e os
/// numeros passam por faixa antes de virar parametro.
///
/// **Recusar, nunca ignorar.** Um filtro desconhecido descartado em silencio
/// devolveria a lista inteira, e quem consultou acreditaria estar vendo so os
/// bloqueios. Numa tela de fraude, acreditar que se filtrou e pior do que
/// receber um erro.
///
/// A busca livre e a unica entrada de texto, e ela e **casada como literal**:
/// os curingas de <c>LIKE</c> sao escapados antes de chegar ao banco. Sem
/// isso, um <c>%</c> digitado por engano devolveria tudo e a tela pareceria
/// filtrada.
/// </summary>
public sealed record FiltroDeTransacoes
{
    /// <summary>
    /// Menor termo de busca aceito.
    ///
    /// Uma letra casaria com quase tudo e faria a consulta varrer a tabela do
    /// tenant para devolver um resultado inutil.
    /// </summary>
    public const int TamanhoMinimoDaBusca = 2;

    public const int TamanhoMaximoDaBusca = 100;

    private FiltroDeTransacoes(
        string? busca,
        Decisao? decisao,
        TipoDeRegra? tipoDeRegra,
        int? scoreMinimo,
        int? scoreMaximo,
        DateTimeOffset? de,
        DateTimeOffset? ate)
    {
        Busca = busca;
        Decisao = decisao;
        TipoDeRegra = tipoDeRegra;
        ScoreMinimo = scoreMinimo;
        ScoreMaximo = scoreMaximo;
        De = de;
        Ate = ate;
    }

    /// <summary>Nenhum filtro: as transacoes do tenant.</summary>
    public static FiltroDeTransacoes Nenhum { get; } = new(null, null, null, null, null, null, null);

    /// <summary>
    /// Texto casado contra o identificador externo e o identificador do
    /// cliente — os dois unicos campos por onde alguem procura uma transacao
    /// especifica durante uma investigacao.
    /// </summary>
    public string? Busca { get; }

    public Decisao? Decisao { get; }

    /// <summary>
    /// Acionou um sinal deste tipo de regra.
    ///
    /// E o filtro que responde "quais transacoes a regra de velocidade
    /// pegou?" — a pergunta que aparece quando alguem quer entender o efeito
    /// de uma regra sem esperar um backtest.
    /// </summary>
    public TipoDeRegra? TipoDeRegra { get; }

    /// <summary>Score da avaliacao, inclusive.</summary>
    public int? ScoreMinimo { get; }

    /// <summary>Score da avaliacao, inclusive.</summary>
    public int? ScoreMaximo { get; }

    /// <summary>
    /// Inicio do periodo, sobre <c>OcorridaEm</c>, inclusive.
    ///
    /// **Sobre a ocorrencia, e nao sobre a chegada.** Quem investiga procura
    /// "o que aconteceu na terca"; uma transacao atrasada aconteceu na terca
    /// mesmo tendo chegado na quinta, e filtrar pela chegada a esconderia
    /// justamente de quem foi procura-la (CLAUDE.md secao 15).
    /// </summary>
    public DateTimeOffset? De { get; }

    /// <summary>Fim do periodo, sobre <c>OcorridaEm</c>, inclusive.</summary>
    public DateTimeOffset? Ate { get; }

    public bool EstaVazio =>
        Busca is null && Decisao is null && TipoDeRegra is null &&
        ScoreMinimo is null && ScoreMaximo is null && De is null && Ate is null;

    /// <summary>
    /// Precisa da avaliacao para responder?
    ///
    /// Serve para o repositorio decidir entre juncao a esquerda e juncao
    /// interna: filtrar por score ou decisao exclui, por definicao, as
    /// transacoes sem avaliacao.
    /// </summary>
    public bool ExigeAvaliacao =>
        Decisao is not null || TipoDeRegra is not null ||
        ScoreMinimo is not null || ScoreMaximo is not null;

    public static bool TentarCriar(
        string? busca,
        string? decisao,
        string? tipoDeRegra,
        int? scoreMinimo,
        int? scoreMaximo,
        DateTimeOffset? de,
        DateTimeOffset? ate,
        out FiltroDeTransacoes filtro,
        out string erro)
    {
        filtro = Nenhum;

        if (!TentarLerBusca(busca, out var buscaResolvida, out erro))
        {
            return false;
        }

        if (!VocabularioFechado.TentarResolver<Decisao>(
                decisao,
                "decisao",
                out var decisaoResolvida,
                out erro))
        {
            return false;
        }

        if (!VocabularioFechado.TentarResolver<TipoDeRegra>(
                tipoDeRegra,
                "tipoDeRegra",
                out var tipoResolvido,
                out erro))
        {
            return false;
        }

        if (!TentarLerFaixaDeScore(scoreMinimo, scoreMaximo, out erro))
        {
            return false;
        }

        if (de is not null && ate is not null && de > ate)
        {
            erro = "O inicio do periodo nao pode ser depois do fim.";
            return false;
        }

        filtro = new FiltroDeTransacoes(
            buscaResolvida,
            decisaoResolvida,
            tipoResolvido,
            scoreMinimo,
            scoreMaximo,
            de,
            ate);

        return true;
    }

    /// <summary>
    /// Prepara o termo para <c>ILIKE</c>, escapando os curingas.
    ///
    /// O escape acontece aqui, e nao no repositorio, para que exista um teste
    /// unitario dizendo o que o produto promete: <c>%</c> e <c>_</c> digitados
    /// pela pessoa sao **texto**, e nao operadores.
    /// </summary>
    public string? PadraoDeBusca =>
        Busca is null
            ? null
            : "%" + Busca
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private static bool TentarLerBusca(string? busca, out string? resolvida, out string erro)
    {
        resolvida = null;
        erro = string.Empty;

        if (string.IsNullOrWhiteSpace(busca))
        {
            return true;
        }

        var limpo = busca.Trim();

        if (limpo.Length is < TamanhoMinimoDaBusca or > TamanhoMaximoDaBusca)
        {
            erro = $"A busca deve ter entre {TamanhoMinimoDaBusca} e {TamanhoMaximoDaBusca} caracteres.";
            return false;
        }

        resolvida = limpo;

        return true;
    }

    private static bool TentarLerFaixaDeScore(int? minimo, int? maximo, out string erro)
    {
        erro = string.Empty;

        if (minimo is < VersaoDePerfilDeRisco.ScoreMinimo or > VersaoDePerfilDeRisco.ScoreMaximo ||
            maximo is < VersaoDePerfilDeRisco.ScoreMinimo or > VersaoDePerfilDeRisco.ScoreMaximo)
        {
            erro = string.Create(
                CultureInfo.InvariantCulture,
                $"O score deve estar entre {VersaoDePerfilDeRisco.ScoreMinimo} e {VersaoDePerfilDeRisco.ScoreMaximo}.");

            return false;
        }

        if (minimo is not null && maximo is not null && minimo > maximo)
        {
            // Faixa invertida devolveria zero linha e pareceria "nao ha nada
            // aqui", quando na verdade a pergunta e que estava errada.
            erro = "O score minimo nao pode ser maior que o maximo.";
            return false;
        }

        return true;
    }
}
