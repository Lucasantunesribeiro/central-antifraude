namespace CentralAntifraude.Application.Comum;

public enum DirecaoDeOrdenacao
{
    Ascendente,
    Descendente,
}

/// <summary>
/// Contrato unico de ordenacao das consultas da Central Antifraude.
///
/// A regra central: o campo de ordenacao NUNCA vem livre do cliente. Cada
/// consulta declara sua propria lista de campos permitidos, e o valor
/// devolvido aqui e sempre o nome canonico dessa lista - nunca o texto que
/// o cliente digitou.
///
/// Isso fecha, ja na Fase 0, o item "ordenacao por campo arbitrario" do
/// Security Gate 10 e impede que um nome de coluna vindo da query string
/// alcance a montagem da consulta.
/// </summary>
public sealed record ParametrosDeOrdenacao
{
    private ParametrosDeOrdenacao(string campo, DirecaoDeOrdenacao direcao)
    {
        Campo = campo;
        Direcao = direcao;
    }

    /// <summary>Nome canonico do campo, vindo da lista de permitidos.</summary>
    public string Campo { get; }

    public DirecaoDeOrdenacao Direcao { get; }

    /// <summary>
    /// Resolve a ordenacao pedida contra a lista de campos permitidos da
    /// consulta. Campo ausente cai no padrao; campo desconhecido e recusado.
    /// </summary>
    /// <param name="campo">Campo pedido pelo cliente, possivelmente nulo.</param>
    /// <param name="direcao">"asc" ou "desc", possivelmente nulo.</param>
    /// <param name="camposPermitidos">Nomes canonicos aceitos por esta consulta.</param>
    /// <param name="campoPadrao">Campo usado quando o cliente nao pede nada.</param>
    public static bool TentarCriar(
        string? campo,
        string? direcao,
        IReadOnlyCollection<string> camposPermitidos,
        string campoPadrao,
        out ParametrosDeOrdenacao parametros,
        out string erro)
    {
        ArgumentNullException.ThrowIfNull(camposPermitidos);
        ArgumentException.ThrowIfNullOrWhiteSpace(campoPadrao);

        if (!camposPermitidos.Contains(campoPadrao, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "O campo padrao precisa estar entre os campos permitidos.",
                nameof(campoPadrao));
        }

        parametros = new ParametrosDeOrdenacao(campoPadrao, DirecaoDeOrdenacao.Descendente);

        if (!TentarResolverDirecao(direcao, out var direcaoResolvida))
        {
            erro = "Direcao de ordenacao invalida. Use 'asc' ou 'desc'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(campo))
        {
            parametros = new ParametrosDeOrdenacao(campoPadrao, direcaoResolvida);
            erro = string.Empty;
            return true;
        }

        var canonico = camposPermitidos.FirstOrDefault(
            permitido => string.Equals(permitido, campo.Trim(), StringComparison.OrdinalIgnoreCase));

        if (canonico is null)
        {
            // A mensagem lista os campos validos de proposito: sao publicos e
            // conhecidos pelo contrato, entao nao ha vazamento de informacao.
            erro = $"Campo de ordenacao invalido. Campos aceitos: {string.Join(", ", camposPermitidos)}.";
            return false;
        }

        parametros = new ParametrosDeOrdenacao(canonico, direcaoResolvida);
        erro = string.Empty;
        return true;
    }

    private static bool TentarResolverDirecao(string? direcao, out DirecaoDeOrdenacao resolvida)
    {
        if (string.IsNullOrWhiteSpace(direcao))
        {
            resolvida = DirecaoDeOrdenacao.Descendente;
            return true;
        }

        switch (direcao.Trim().ToLowerInvariant())
        {
            case "asc":
                resolvida = DirecaoDeOrdenacao.Ascendente;
                return true;
            case "desc":
                resolvida = DirecaoDeOrdenacao.Descendente;
                return true;
            default:
                resolvida = DirecaoDeOrdenacao.Descendente;
                return false;
        }
    }
}
