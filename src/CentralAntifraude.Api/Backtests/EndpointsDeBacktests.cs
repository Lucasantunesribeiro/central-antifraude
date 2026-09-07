using CentralAntifraude.Api.Comum;
using CentralAntifraude.Api.Identidade;
using CentralAntifraude.Application.Backtests;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Erros;
using Microsoft.AspNetCore.Mvc;

namespace CentralAntifraude.Api.Backtests;

/// <summary>
/// Simulacao historica de um perfil candidato.
///
/// **Tudo aqui e supervisao.** Gerir regras e executar backtests sao a mesma
/// atribuicao (CLAUDE.md secao 8.2), e um backtest nao e uma decisao: e um
/// ensaio sobre uma decisao que ainda nao foi tomada. O material do Auditor
/// sao as decisoes reais e a trilha — nao os ensaios de quem supervisiona.
///
/// **O POST nao executa nada** (ROADMAP 9.3). Ele congela o candidato, grava a
/// execucao e o evento na mesma transacao e responde `202`. O trabalho
/// acontece no worker da fila dedicada.
/// </summary>
public static class EndpointsDeBacktests
{
    private const string Caminho = "/backtests";

    public static void MapearEndpointsDeBacktests(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);

        var grupo = rotas
            .MapGroup($"{SessaoHttp.PrefixoDaApi}{Caminho}")
            .WithTags("Backtests")
            .RequireAuthorization(PoliticasDeAutorizacao.Supervisor);

        grupo.MapGet("/", async (
                [FromQuery] int? pagina,
                [FromQuery] int? tamanho,
                ServicoDeBacktests servico,
                CancellationToken cancellationToken) =>
            {
                if (!ParametrosDePaginacao.TentarCriar(pagina, tamanho, out var paginacao, out var erro))
                {
                    throw new ErroDeValidacao("paginacao", erro);
                }

                return Results.Ok(PaginaDeBacktests.De(
                    await servico.ListarAsync(paginacao, cancellationToken)));
            })
            .WithName("ListarBacktests");

        grupo.MapGet("/{id:guid}", async (
                Guid id,
                ServicoDeBacktests servico,
                CancellationToken cancellationToken) =>
                Results.Ok(BacktestDetalhado.De(await servico.ObterAsync(id, cancellationToken))))
            .WithName("ObterBacktest");

        grupo.MapPost("/", async (
                SolicitarBacktestRequisicao requisicao,
                ServicoDeBacktests servico,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(requisicao);

                var execucao = await servico.SolicitarAsync(
                    requisicao.RegraId,
                    requisicao.LimiarDeRevisao,
                    requisicao.LimiarDeBloqueio,
                    requisicao.Inicio,
                    requisicao.Fim,
                    cancellationToken);

                // 202, e nao 201: o recurso existe, mas o que foi pedido ainda
                // nao aconteceu. Um 201 faria o cliente acreditar que o
                // resultado esta pronto no corpo.
                return Results.Accepted(
                    $"{SessaoHttp.PrefixoDaApi}{Caminho}/{execucao.Id}",
                    BacktestDetalhado.De(execucao));
            })
            .WithName("SolicitarBacktest");

        grupo.MapPost("/{id:guid}/cancelamento", async (
                Guid id,
                CancelarBacktestRequisicao requisicao,
                ServicoDeBacktests servico,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(requisicao);

                var execucao = await servico.CancelarAsync(id, requisicao.Versao, cancellationToken);

                return Results.Ok(BacktestDetalhado.De(execucao));
            })
            .WithName("CancelarBacktest");
    }
}
