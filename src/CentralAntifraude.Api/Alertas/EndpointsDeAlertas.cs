using CentralAntifraude.Api.Comum;
using CentralAntifraude.Api.Identidade;
using CentralAntifraude.Application.Alertas;
using CentralAntifraude.Application.Erros;
using Microsoft.AspNetCore.Mvc;

namespace CentralAntifraude.Api.Alertas;

/// <summary>
/// A fila operacional de alertas.
///
/// **Somente leitura, e de proposito.** Nao ha rota que crie, altere, atribua,
/// feche ou apague alerta — e o Security Gate 6 verifica essa ausencia com
/// teste. Alerta e resultado de avaliacao, produzido pelo consumidor; abrir
/// escrita agora criaria um caminho para inventar trabalho de investigacao a
/// partir de um JSON, sem avaliacao, sem sinais e sem explicacao.
///
/// A acao humana sobre um alerta — assumir, investigar, resolver — pertence ao
/// **caso**, que e a Fase 7. Ate la o alerta nasce aberto e assim permanece.
///
/// Nao ha rota de detalhe pelo mesmo criterio: o ROADMAP 6.6 pede navegacao do
/// alerta para a transacao, a avaliacao e os sinais — e essa tela ja existe
/// desde a Fase 3. Cada linha da fila carrega o <c>transacaoId</c>, e o
/// caminho termina la.
/// </summary>
public static class EndpointsDeAlertas
{
    public static void MapearEndpointsDeAlertas(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);

        // Qualquer perfil autenticado le a fila da propria organizacao,
        // inclusive o Auditor: consultar decisoes e trilha e exatamente o
        // trabalho dele (`CLAUDE.md` secao 8.3). O que o Auditor nao pode e
        // agir — e aqui nao ha o que agir.
        var grupo = rotas
            .MapGroup($"{SessaoHttp.PrefixoDaApi}/alertas")
            .WithTags("Alertas")
            .RequireAuthorization(PoliticasDeAutorizacao.QualquerPerfil);

        grupo.MapGet("/", async (
                [FromQuery] int? pagina,
                [FromQuery] int? tamanho,
                [FromQuery] string? ordenarPor,
                [FromQuery] string? direcao,
                [FromQuery] string? decisao,
                [FromQuery] string? prioridade,
                [FromQuery] int? scoreMinimo,
                [FromQuery] DateTimeOffset? de,
                [FromQuery] DateTimeOffset? ate,
                ServicoDeConsultaDeAlertas servico,
                CancellationToken cancellationToken) =>
            {
                var (paginacao, ordenacao) = ParametrosDeConsultaHttp.Ler(
                    pagina,
                    tamanho,
                    ordenarPor,
                    direcao,
                    ServicoDeConsultaDeAlertas.CamposDeOrdenacao,
                    ServicoDeConsultaDeAlertas.OrdenacaoPadrao);

                // Filtro desconhecido e recusado, nunca ignorado. Descartar em
                // silencio devolveria a fila inteira e o analista acreditaria
                // estar vendo so os bloqueios.
                if (!FiltroDeAlertas.TentarCriar(
                        decisao,
                        prioridade,
                        scoreMinimo,
                        de,
                        ate,
                        out var filtro,
                        out var erro))
                {
                    throw new ErroDeValidacao("filtro", erro);
                }

                var resultado = await servico.ListarAsync(
                    filtro,
                    paginacao,
                    ordenacao,
                    cancellationToken);

                return Results.Ok(RespostaPaginada.De(resultado, AlertaResumido.De));
            })
            .WithName("ListarAlertas");
    }
}
