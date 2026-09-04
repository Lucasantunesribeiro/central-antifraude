using CentralAntifraude.Api.Identidade;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Risco;

namespace CentralAntifraude.Api.Risco;

/// <summary>
/// Consulta do catalogo de regras e do perfil vigente.
///
/// Somente leitura, e de proposito. Criar, editar ou publicar regra e a Fase 8
/// — e vai exigir perfil de Supervisor, rascunho e backtest antes da
/// publicacao (CLAUDE.md secao 23). Expor escrita agora criaria um caminho
/// para alterar o comportamento do motor sem nenhuma dessas protecoes.
/// </summary>
public static class EndpointsDeRisco
{
    public static void MapearEndpointsDeRisco(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);

        var grupo = rotas
            .MapGroup($"{SessaoHttp.PrefixoDaApi}/regras")
            .WithTags("Regras")
            .RequireAuthorization(PoliticasDeAutorizacao.QualquerPerfil);

        grupo.MapGet("/", async (
                ServicoDeConsultaDeRisco servico,
                CancellationToken cancellationToken) =>
            {
                var regras = await servico.ListarRegrasAsync(cancellationToken);

                return Results.Ok(regras.Select(par => RegraResposta.De(par.Regra, par.Versao)).ToList());
            })
            .WithName("ListarRegras");

        grupo.MapGet("/perfil", async (
                ServicoDeConsultaDeRisco servico,
                CancellationToken cancellationToken) =>
            {
                var perfil = await servico.ObterPerfilVigenteAsync(cancellationToken)
                    // 404 e a resposta honesta: nao ha perfil publicado para
                    // este tenant. Devolver um perfil vazio com limiares zero
                    // faria a tela mostrar limiares que nao existem.
                    ?? throw new RecursoNaoEncontrado("Perfil de risco");

                var regras = await servico.ListarRegrasAsync(cancellationToken);

                return Results.Ok(new PerfilVigenteResposta(
                    perfil.Id,
                    perfil.Numero,
                    perfil.LimiarDeRevisao,
                    perfil.LimiarDeBloqueio,
                    perfil.PublicadaEm,
                    regras.Select(par => RegraResposta.De(par.Regra, par.Versao)).ToList()));
            })
            .WithName("ObterPerfilDeRiscoVigente");
    }
}
