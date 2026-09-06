using CentralAntifraude.Api.Comum;
using CentralAntifraude.Api.Identidade;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Investigacao;
using CentralAntifraude.Domain.Investigacao;
using Microsoft.AspNetCore.Mvc;

namespace CentralAntifraude.Api.Investigacao;

/// <summary>
/// A investigacao humana.
///
/// **Leitura para todos, acao so para quem opera.** O Auditor le a lista e o
/// workspace inteiro — consultar decisoes e trilha e o trabalho dele — e nao
/// alcanca nenhuma rota que mude alguma coisa. Isso nao depende de esconder
/// botao: cada rota de escrita declara a politica, e o servico confere o
/// perfil de novo.
///
/// **Rotas de acao, e nao um `PUT` no recurso inteiro.** `assumir`,
/// `transferir`, `resolver` sao transicoes de fluxo com regras proprias; um
/// `PUT /casos/{id}` aceitando o objeto inteiro convidaria justamente ao mass
/// assignment que o produto recusa — bastaria mandar `status` e `resultado` no
/// corpo.
/// </summary>
public static class EndpointsDeCasos
{
    public static void MapearEndpointsDeCasos(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);

        var grupo = rotas
            .MapGroup($"{SessaoHttp.PrefixoDaApi}/casos")
            .WithTags("Casos")
            .RequireAuthorization(PoliticasDeAutorizacao.QualquerPerfil);

        MapearLeitura(grupo);
        MapearEscrita(grupo);
    }

    private static void MapearLeitura(RouteGroupBuilder grupo)
    {
        grupo.MapGet("/", async (
                [FromQuery] int? pagina,
                [FromQuery] int? tamanho,
                [FromQuery] string? ordenarPor,
                [FromQuery] string? direcao,
                [FromQuery] string? status,
                [FromQuery] string? resultado,
                [FromQuery] Guid? responsavelId,
                [FromQuery] bool? semResponsavel,
                ServicoDeCasos servico,
                CancellationToken cancellationToken) =>
            {
                var (paginacao, ordenacao) = ParametrosDeConsultaHttp.Ler(
                    pagina,
                    tamanho,
                    ordenarPor,
                    direcao,
                    ServicoDeCasos.CamposDeOrdenacao,
                    ServicoDeCasos.OrdenacaoPadrao);

                if (!FiltroDeCasos.TentarCriar(
                        status,
                        resultado,
                        responsavelId,
                        semResponsavel ?? false,
                        out var filtro,
                        out var erro))
                {
                    throw new ErroDeValidacao("filtro", erro);
                }

                var resposta = await servico.ListarAsync(filtro, paginacao, ordenacao, cancellationToken);

                return Results.Ok(RespostaPaginada.De(resposta, CasoResumido.De));
            })
            .WithName("ListarCasos");

        grupo.MapGet("/{id:guid}", async (
                Guid id,
                ServicoDeCasos servico,
                CancellationToken cancellationToken) =>
                Results.Ok(CasoDetalhado.De(await servico.ObterAsync(id, cancellationToken))))
            .WithName("ObterCaso");
    }

    private static void MapearEscrita(RouteGroupBuilder grupo)
    {
        // Agir sobre um caso e operacao de fraude. O Auditor nao entra aqui —
        // e a politica ja o recusa antes de qualquer codigo desta fase rodar.
        var operacao = grupo
            .MapGroup(string.Empty)
            .RequireAuthorization(PoliticasDeAutorizacao.OperacaoDeFraude);

        operacao.MapPost("/", async (
                AbrirCasoRequisicao requisicao,
                ServicoDeCasos servico,
                HttpContext contexto,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(requisicao);

                var caso = await servico.AbrirAsync(
                    requisicao.Titulo,
                    requisicao.AlertasIds ?? [],
                    cancellationToken);

                var detalhe = await servico.ObterAsync(caso.Id, cancellationToken);

                return Results.Created(
                    $"{SessaoHttp.PrefixoDaApi}/casos/{caso.Id}",
                    CasoDetalhado.De(detalhe));
            })
            .WithName("AbrirCaso");

        operacao.MapPost("/{id:guid}/alertas", async (
                Guid id,
                AssociarAlertaRequisicao requisicao,
                ServicoDeCasos servico,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(requisicao);

                await servico.AssociarAlertaAsync(
                    id,
                    requisicao.AlertaId,
                    requisicao.Versao,
                    cancellationToken);

                return Results.Ok(CasoDetalhado.De(await servico.ObterAsync(id, cancellationToken)));
            })
            .WithName("AssociarAlertaAoCaso");

        operacao.MapPost("/{id:guid}/assumir", async (
                Guid id,
                AssumirCasoRequisicao requisicao,
                ServicoDeCasos servico,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(requisicao);

                await servico.AssumirAsync(id, requisicao.Versao, cancellationToken);

                return Results.Ok(CasoDetalhado.De(await servico.ObterAsync(id, cancellationToken)));
            })
            .WithName("AssumirCaso");

        operacao.MapPost("/{id:guid}/transferir", async (
                Guid id,
                TransferirCasoRequisicao requisicao,
                ServicoDeCasos servico,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(requisicao);

                // O perfil de supervisao e conferido no servico, e nao aqui:
                // transferir e a unica acao desta fase com regra propria de
                // perfil, e uma politica so para ela ficaria escondida da
                // leitura do fluxo.
                await servico.TransferirAsync(
                    id,
                    requisicao.ParaUsuarioId,
                    requisicao.Versao,
                    cancellationToken);

                return Results.Ok(CasoDetalhado.De(await servico.ObterAsync(id, cancellationToken)));
            })
            .WithName("TransferirCaso");

        operacao.MapPost("/{id:guid}/notas", async (
                Guid id,
                NotaRequisicao requisicao,
                ServicoDeCasos servico,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(requisicao);

                var nota = await servico.AdicionarNotaAsync(
                    id,
                    requisicao.Conteudo,
                    requisicao.Versao,
                    cancellationToken);

                return Results.Created(
                    $"{SessaoHttp.PrefixoDaApi}/casos/{id}",
                    NotaResposta.De(nota));
            })
            .WithName("AdicionarNotaAoCaso");

        operacao.MapPost("/{id:guid}/resolucao", async (
                Guid id,
                ResolverCasoRequisicao requisicao,
                ServicoDeCasos servico,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(requisicao);

                // O resultado e vocabulario fechado. Um valor fora da lista e
                // recusado com 400 — nunca cai num padrao, porque "resultado
                // padrao de investigacao" nao existe: alguem precisa decidir.
                if (!VocabularioFechado.TentarResolver<ResultadoDaInvestigacao>(
                        requisicao.Resultado,
                        "resultado",
                        out var resultado,
                        out var erro) ||
                    resultado is null)
                {
                    throw new ErroDeValidacao(
                        "resultado",
                        string.IsNullOrEmpty(erro)
                            ? "Informe o resultado da investigacao."
                            : erro);
                }

                await servico.ResolverAsync(id, resultado.Value, requisicao.Versao, cancellationToken);

                return Results.Ok(CasoDetalhado.De(await servico.ObterAsync(id, cancellationToken)));
            })
            .WithName("ResolverCaso");
    }
}
