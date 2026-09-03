namespace CentralAntifraude.Application.Comum;

/// <summary>
/// Contrato unico de paginacao das consultas da Central Antifraude.
///
/// Decisao congelada na Fase 0 (docs/adr/0004-contratos-transversais.md):
/// paginacao e por numero de pagina + tamanho, com teto rigido.
///
/// A validacao e estrita de proposito: um pedido de 5.000 registros nao e
/// silenciosamente reduzido para 100, ele e recusado. Reduzir em silencio
/// faria o cliente acreditar que recebeu a lista inteira - e, numa tela de
/// alertas de fraude, acreditar que viu tudo e pior do que receber um erro.
/// </summary>
public sealed record ParametrosDePaginacao
{
    public const int TamanhoPadrao = 25;
    public const int TamanhoMaximo = 100;

    private ParametrosDePaginacao(int pagina, int tamanho)
    {
        Pagina = pagina;
        Tamanho = tamanho;
    }

    /// <summary>Pagina solicitada, comecando em 1.</summary>
    public int Pagina { get; }

    /// <summary>Quantidade de itens por pagina.</summary>
    public int Tamanho { get; }

    /// <summary>Deslocamento correspondente, para uso em consultas.</summary>
    public int QuantidadeAPular => (Pagina - 1) * Tamanho;

    /// <summary>Primeira pagina com o tamanho padrao.</summary>
    public static ParametrosDePaginacao Padrao { get; } = new(1, TamanhoPadrao);

    /// <summary>
    /// Valida e cria os parametros. Valores ausentes caem no padrao;
    /// valores fora da faixa sao recusados com mensagem explicita.
    /// </summary>
    public static bool TentarCriar(
        int? pagina,
        int? tamanho,
        out ParametrosDePaginacao parametros,
        out string erro)
    {
        parametros = Padrao;

        var paginaEfetiva = pagina ?? 1;
        var tamanhoEfetivo = tamanho ?? TamanhoPadrao;

        if (paginaEfetiva < 1)
        {
            erro = "A pagina deve ser maior ou igual a 1.";
            return false;
        }

        if (tamanhoEfetivo < 1 || tamanhoEfetivo > TamanhoMaximo)
        {
            erro = $"O tamanho da pagina deve estar entre 1 e {TamanhoMaximo}.";
            return false;
        }

        parametros = new ParametrosDePaginacao(paginaEfetiva, tamanhoEfetivo);
        erro = string.Empty;
        return true;
    }
}
