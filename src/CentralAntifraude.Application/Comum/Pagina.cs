namespace CentralAntifraude.Application.Comum;

/// <summary>
/// Resultado paginado devolvido pelas consultas.
///
/// Carrega o total real de itens porque as telas operacionais precisam saber
/// quantos alertas ou casos existem, nao apenas os da pagina atual.
/// </summary>
public sealed record Pagina<T>
{
    public Pagina(IReadOnlyList<T> itens, ParametrosDePaginacao parametros, long totalDeItens)
    {
        ArgumentNullException.ThrowIfNull(itens);
        ArgumentNullException.ThrowIfNull(parametros);
        ArgumentOutOfRangeException.ThrowIfNegative(totalDeItens);

        Itens = itens;
        PaginaAtual = parametros.Pagina;
        TamanhoDaPagina = parametros.Tamanho;
        TotalDeItens = totalDeItens;
    }

    public IReadOnlyList<T> Itens { get; }

    public int PaginaAtual { get; }

    public int TamanhoDaPagina { get; }

    public long TotalDeItens { get; }

    public int TotalDePaginas =>
        TotalDeItens == 0 ? 0 : (int)Math.Ceiling(TotalDeItens / (double)TamanhoDaPagina);

    public bool TemProximaPagina => PaginaAtual < TotalDePaginas;
}
