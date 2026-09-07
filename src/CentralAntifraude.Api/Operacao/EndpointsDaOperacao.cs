using CentralAntifraude.Api.Identidade;
using CentralAntifraude.Application.Auditoria;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Operacao;
using CentralAntifraude.Domain.Tempo;
using Microsoft.AspNetCore.Mvc;

namespace CentralAntifraude.Api.Operacao;

/// <summary>
/// Painel operacional e trilha de auditoria.
///
/// **O painel e leitura para qualquer perfil autenticado.** Ele resume o que a
/// organizacao ja pode ver nas telas de transacoes, alertas e casos — esconder
/// o resumo de quem enxerga as partes seria teatro, e nao autorizacao.
///
/// **A trilha nao.** Ela e um controle sobre quem opera, e por isso responde
/// apenas a Administrador e Auditor (CLAUDE.md secoes 8.3 e 67).
///
/// **Nada aqui aceita organizacao por parametro.** Todo numero sai de consulta
/// filtrada pelo tenant da identidade; um painel que somasse organizacoes
/// devolveria numeros plausiveis, e nada denunciaria.
/// </summary>
public static class EndpointsDaOperacao
{
    public static void MapearEndpointsDoPainel(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);

        var grupo = rotas
            .MapGroup($"{SessaoHttp.PrefixoDaApi}/painel")
            .WithTags("Painel")
            .RequireAuthorization(PoliticasDeAutorizacao.QualquerPerfil);

        grupo.MapGet("/", async (
                [FromQuery] int? dias,
                ServicoDoPainel servico,
                IRelogio relogio,
                CancellationToken cancellationToken) =>
            {
                var janela = LerJanela(dias, relogio);

                return Results.Ok(PainelResposta.De(
                    await servico.ResumirAsync(janela, cancellationToken)));
            })
            .WithName("ObterPainelOperacional");

        grupo.MapGet("/regras", async (
                [FromQuery] int? dias,
                ServicoDoPainel servico,
                IRelogio relogio,
                CancellationToken cancellationToken) =>
            {
                var janela = LerJanela(dias, relogio);

                var metricas = await servico.ApurarMetricasDeRegraAsync(janela, cancellationToken);

                return Results.Ok(metricas.Select(MetricaDeRegraResposta.De).ToList());
            })
            .WithName("ObterMetricasDeRegra");
    }

    public static void MapearEndpointsDeAuditoria(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);

        var grupo = rotas
            .MapGroup($"{SessaoHttp.PrefixoDaApi}/auditoria")
            .WithTags("Auditoria")
            .RequireAuthorization(PoliticasDeAutorizacao.LeituraDeAuditoria);

        // Somente GET. Nao existe rota que crie, altere ou apague um registro
        // de auditoria — a trilha e somente insercao, e quem insere sao as
        // operacoes que ela registra (CLAUDE.md secao 67).
        grupo.MapGet("/", async (
                [FromQuery] string? operacao,
                [FromQuery] Guid? autorId,
                [FromQuery] Guid? entidadeId,
                [FromQuery] DateTimeOffset? de,
                [FromQuery] DateTimeOffset? ate,
                [FromQuery] int? pagina,
                [FromQuery] int? tamanho,
                ServicoDeAuditoria servico,
                CancellationToken cancellationToken) =>
            {
                if (!FiltroDeAuditoria.TentarCriar(
                        operacao,
                        autorId,
                        entidadeId,
                        de,
                        ate,
                        out var filtro,
                        out var erroDoFiltro))
                {
                    throw new ErroDeValidacao("filtro", erroDoFiltro);
                }

                if (!ParametrosDePaginacao.TentarCriar(
                        pagina,
                        tamanho,
                        out var paginacao,
                        out var erroDaPaginacao))
                {
                    throw new ErroDeValidacao("paginacao", erroDaPaginacao);
                }

                return Results.Ok(PaginaDeAuditoria.De(
                    await servico.ListarAsync(filtro, paginacao, cancellationToken)));
            })
            .WithName("ListarAuditoria");

        grupo.MapGet("/operacoes", async (
                ServicoDeAuditoria servico,
                CancellationToken cancellationToken) =>
            {
                var operacoes = await servico.ListarOperacoesUsadasAsync(cancellationToken);

                return Results.Ok(operacoes.Select(o => o.ToString()).ToList());
            })
            .WithName("ListarOperacoesAuditadas");
    }

    /// <summary>
    /// Le a janela do painel, recusando periodo fora da faixa.
    ///
    /// Recusar, e nao limitar em silencio: um pedido de dez anos reduzido para
    /// noventa dias devolveria numeros que nao respondem a pergunta feita.
    /// </summary>
    private static JanelaDoPainel LerJanela(int? dias, IRelogio relogio)
    {
        ArgumentNullException.ThrowIfNull(relogio);

        if (!JanelaDoPainel.TentarCriar(dias, relogio.Agora, out var janela, out var erro))
        {
            throw new ErroDeValidacao("dias", erro);
        }

        return janela;
    }
}
