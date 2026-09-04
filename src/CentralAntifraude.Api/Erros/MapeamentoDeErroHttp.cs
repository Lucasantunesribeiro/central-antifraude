using CentralAntifraude.Application.Erros;
using Microsoft.AspNetCore.Http;

namespace CentralAntifraude.Api.Erros;

/// <summary>
/// Tabela unica de traducao entre a categoria de erro da aplicacao e o
/// contrato HTTP.
///
/// Isolada do tratador para poder ser testada sozinha: e ela que garante que
/// "recurso de outro tenant" continue saindo como 404 e nao vire 403 numa
/// refatoracao distraida (CLAUDE.md secao 52).
/// </summary>
public static class MapeamentoDeErroHttp
{
    public static int StatusPara(TipoDeErro tipo) => tipo switch
    {
        TipoDeErro.Validacao => StatusCodes.Status400BadRequest,
        TipoDeErro.NaoAutenticado => StatusCodes.Status401Unauthorized,
        TipoDeErro.NaoAutorizado => StatusCodes.Status403Forbidden,
        TipoDeErro.NaoEncontrado => StatusCodes.Status404NotFound,
        TipoDeErro.Conflito => StatusCodes.Status409Conflict,
        TipoDeErro.LimiteDeRequisicoes => StatusCodes.Status429TooManyRequests,
        TipoDeErro.Interno => StatusCodes.Status500InternalServerError,
        TipoDeErro.Indisponivel => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static string TituloPara(TipoDeErro tipo) => tipo switch
    {
        TipoDeErro.Validacao => "Requisicao invalida",
        TipoDeErro.NaoAutenticado => "Nao autenticado",
        TipoDeErro.NaoAutorizado => "Nao autorizado",
        TipoDeErro.NaoEncontrado => "Recurso nao encontrado",
        TipoDeErro.Conflito => "Conflito de estado",
        TipoDeErro.LimiteDeRequisicoes => "Limite de requisicoes excedido",
        TipoDeErro.Interno => "Falha interna",
        TipoDeErro.Indisponivel => "Servico indisponivel no momento",
        _ => "Falha interna",
    };
}
