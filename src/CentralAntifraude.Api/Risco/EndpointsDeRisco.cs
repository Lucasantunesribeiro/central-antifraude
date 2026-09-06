using CentralAntifraude.Api.Identidade;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Risco;
using CentralAntifraude.Domain.Risco;
using Microsoft.AspNetCore.Mvc;

namespace CentralAntifraude.Api.Risco;

/// <summary>
/// Consulta e administracao do catalogo de regras e do perfil de risco.
///
/// **Leitura para todos, administracao so para a supervisao.** O analista
/// precisa ver as regras vigentes para entender o proprio score; mudar o que o
/// motor executa e ato de supervisao (CLAUDE.md secao 8.2). A separacao esta
/// nas politicas das rotas, e o servico confere o perfil de novo.
///
/// **Rotas de acao, e nao um `PUT` no recurso.** Nao existe
/// <c>PUT /api/regras/{id}</c>: um corpo com o objeto inteiro convidaria ao
/// mass assignment — bastaria mandar <c>numeroDaUltimaVersao</c> ou
/// <c>ativa</c>. Cada transicao tem rota propria, com a sua regra.
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

        MapearLeituraOperacional(grupo);
        MapearAdministracao(grupo);
    }

    private static void MapearLeituraOperacional(RouteGroupBuilder grupo)
    {
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

    private static void MapearAdministracao(RouteGroupBuilder grupo)
    {
        var supervisao = grupo
            .MapGroup(string.Empty)
            .RequireAuthorization(PoliticasDeAutorizacao.Supervisor);

        MapearCatalogoELeitura(supervisao);
        MapearRascunhoEPublicacao(supervisao);

        supervisao.MapPost("/perfil/limiares", async (
                LimiaresRequisicao requisicao,
                ServicoDeGestaoDeRegras servico,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(requisicao);

                var versao = await servico.PublicarLimiaresAsync(
                    requisicao.LimiarDeRevisao,
                    requisicao.LimiarDeBloqueio,
                    requisicao.NumeroDaVersaoVigente,
                    cancellationToken);

                return Results.Created(
                    $"{SessaoHttp.PrefixoDaApi}/regras/perfil",
                    new { versao.Id, versao.Numero, versao.LimiarDeRevisao, versao.LimiarDeBloqueio });
            })
            .WithName("PublicarLimiaresDoPerfil");
    }

    private static void MapearCatalogoELeitura(RouteGroupBuilder supervisao)
    {
        // O catalogo fechado, descrito. E daqui que a tela monta o formulario:
        // sem esta rota o frontend repetiria os limites de cada campo, e as
        // duas listas sairiam de sincronia no primeiro ajuste (ROADMAP 8.3).
        supervisao.MapGet("/tipos", () =>
                Results.Ok(CatalogoDeTiposDeRegra.Todos.Select(TipoDeRegraResposta.De).ToList()))
            .WithName("ListarTiposDeRegra");

        supervisao.MapGet("/gestao", async (
                ServicoDeGestaoDeRegras servico,
                CancellationToken cancellationToken) =>
            {
                var regras = await servico.ListarAsync(cancellationToken);

                return Results.Ok(regras.Select(RegraAdministradaResposta.De).ToList());
            })
            .WithName("ListarRegrasParaAdministracao");

        supervisao.MapGet("/{id:guid}", async (
                Guid id,
                ServicoDeGestaoDeRegras servico,
                CancellationToken cancellationToken) =>
                Results.Ok(RegraAdministradaResposta.De(await servico.ObterAsync(id, cancellationToken))))
            .WithName("ObterRegra");
    }

    private static void MapearRascunhoEPublicacao(RouteGroupBuilder supervisao)
    {
        supervisao.MapPost("/", async (
                CriarRegraRequisicao requisicao,
                ServicoDeGestaoDeRegras servico,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(requisicao);

                var (tipo, configuracao) = LerTipoEConfiguracao(
                    requisicao.Tipo,
                    requisicao.Configuracao);

                var regra = await servico.CriarAsync(
                    tipo,
                    requisicao.Nome ?? string.Empty,
                    configuracao,
                    requisicao.Pontos,
                    cancellationToken);

                var detalhe = await servico.ObterAsync(regra.Id, cancellationToken);

                return Results.Created(
                    $"{SessaoHttp.PrefixoDaApi}/regras/{regra.Id}",
                    RegraAdministradaResposta.De(detalhe));
            })
            .WithName("CriarRegra");

        supervisao.MapPut("/{id:guid}/rascunho", async (
                Guid id,
                RascunhoDeRegraRequisicao requisicao,
                ServicoDeGestaoDeRegras servico,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(requisicao);

                var regra = await servico.ObterAsync(id, cancellationToken);

                // O tipo nao vem no corpo: ele e da identidade da regra e nao
                // muda. Uma regra que trocasse de tipo seria outra regra, e as
                // versoes antigas passariam a explicar algo que nao existe
                // mais.
                var (_, configuracao) = LerTipoEConfiguracao(
                    regra.Regra.Tipo.ToString(),
                    requisicao.Configuracao);

                await servico.SalvarRascunhoAsync(
                    id,
                    requisicao.Nome ?? string.Empty,
                    configuracao,
                    requisicao.Pontos,
                    requisicao.Versao,
                    cancellationToken);

                return Results.Ok(RegraAdministradaResposta.De(
                    await servico.ObterAsync(id, cancellationToken)));
            })
            .WithName("SalvarRascunhoDeRegra");

        supervisao.MapDelete("/{id:guid}/rascunho", async (
                Guid id,
                [FromQuery] int? versao,
                ServicoDeGestaoDeRegras servico,
                CancellationToken cancellationToken) =>
            {
                if (versao is null)
                {
                    throw new ErroDeValidacao(
                        "versao",
                        "Informe a versao da regra que voce esta vendo.");
                }

                await servico.DescartarRascunhoAsync(id, versao.Value, cancellationToken);

                return Results.Ok(RegraAdministradaResposta.De(
                    await servico.ObterAsync(id, cancellationToken)));
            })
            .WithName("DescartarRascunhoDeRegra");

        supervisao.MapPost("/{id:guid}/publicacao", async (
                Guid id,
                AcaoNaRegraRequisicao requisicao,
                ServicoDeGestaoDeRegras servico,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(requisicao);

                await servico.PublicarAsync(id, requisicao.Versao, cancellationToken);

                return Results.Created(
                    $"{SessaoHttp.PrefixoDaApi}/regras/{id}",
                    RegraAdministradaResposta.De(await servico.ObterAsync(id, cancellationToken)));
            })
            .WithName("PublicarVersaoDeRegra");

        supervisao.MapPost("/{id:guid}/ativacao", async (
                Guid id,
                AtivacaoDaRegraRequisicao requisicao,
                ServicoDeGestaoDeRegras servico,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(requisicao);

                await servico.DefinirAtivacaoAsync(
                    id,
                    requisicao.Ativa,
                    requisicao.Versao,
                    cancellationToken);

                return Results.Ok(RegraAdministradaResposta.De(
                    await servico.ObterAsync(id, cancellationToken)));
            })
            .WithName("DefinirAtivacaoDaRegra");
    }

    /// <summary>
    /// Traduz tipo e numeros do corpo para o contrato tipado do dominio.
    ///
    /// Os dois erros possiveis sao <c>400</c>, e nao <c>409</c>: tipo fora do
    /// catalogo e configuracao invalida nao sao estado incompativel do
    /// recurso, e sim conteudo que nao serve.
    /// </summary>
    private static (TipoDeRegra Tipo, ConfiguracaoDeRegra Configuracao) LerTipoEConfiguracao(
        string? tipoBruto,
        Dictionary<string, decimal>? valores)
    {
        if (!VocabularioFechado.TentarResolver<TipoDeRegra>(
                tipoBruto,
                "tipo",
                out var tipo,
                out var erroDoTipo) ||
            tipo is null)
        {
            throw new ErroDeValidacao(
                "tipo",
                string.IsNullOrEmpty(erroDoTipo) ? "Informe o tipo da regra." : erroDoTipo);
        }

        if (!CatalogoDeTiposDeRegra.TentarMontar(
                tipo.Value,
                valores,
                out var configuracao,
                out var erroDaConfiguracao) ||
            configuracao is null)
        {
            throw new ErroDeValidacao("configuracao", erroDaConfiguracao);
        }

        return (tipo.Value, configuracao);
    }
}
