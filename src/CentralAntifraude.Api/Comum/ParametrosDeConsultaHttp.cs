using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Erros;

namespace CentralAntifraude.Api.Comum;

/// <summary>
/// Traducao unica de query string para os contratos de consulta.
///
/// Existe para que toda listagem do produto recuse da mesma forma: pagina fora
/// da faixa, tamanho acima do teto e campo de ordenacao desconhecido viram
/// `400` com mensagem, e nunca sao silenciosamente corrigidos. Uma tela que
/// pede ordenacao por um campo que nao existe e recebe a ordem padrao sem
/// aviso esconde o proprio defeito.
/// </summary>
public static class ParametrosDeConsultaHttp
{
    public static (ParametrosDePaginacao Paginacao, ParametrosDeOrdenacao Ordenacao) Ler(
        int? pagina,
        int? tamanho,
        string? ordenarPor,
        string? direcao,
        IReadOnlyCollection<string> camposPermitidos,
        string campoPadrao)
    {
        if (!ParametrosDePaginacao.TentarCriar(pagina, tamanho, out var paginacao, out var erroPaginacao))
        {
            throw new ErroDeValidacao("paginacao", erroPaginacao);
        }

        if (!ParametrosDeOrdenacao.TentarCriar(
                ordenarPor,
                direcao,
                camposPermitidos,
                campoPadrao,
                out var ordenacao,
                out var erroOrdenacao))
        {
            throw new ErroDeValidacao("ordenacao", erroOrdenacao);
        }

        return (paginacao, ordenacao);
    }
}
