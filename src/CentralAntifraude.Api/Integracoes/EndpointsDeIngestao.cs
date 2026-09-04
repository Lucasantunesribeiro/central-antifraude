using CentralAntifraude.Api.Identidade;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Integracoes;
using CentralAntifraude.Application.Transacoes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CentralAntifraude.Api.Integracoes;

public static class EndpointsDeIngestao
{
    /// <summary>Politica de limite aplicada a ingestao.</summary>
    public const string LimiteDeIngestao = "ingestao";

    /// <summary>Cabecalho da chave de idempotencia.</summary>
    public const string CabecalhoDeIdempotencia = "Idempotency-Key";

    /// <summary>
    /// Teto do corpo da requisicao de ingestao.
    ///
    /// O contrato inteiro cabe em algumas centenas de bytes. 8 KB e folga
    /// generosa e ainda assim fecha a porta para um corpo de megabytes que
    /// consumiria memoria antes de qualquer validacao rodar.
    /// </summary>
    public const long TamanhoMaximoDoCorpo = 8 * 1024;

    /// <summary>Politica que exige uma integracao autenticada.</summary>
    public const string PoliticaDeIntegracao = "integracao:autenticada";

    public static void RegistrarPoliticaDeIntegracao(AuthorizationOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        opcoes.AddPolicy(PoliticaDeIntegracao, politica => politica
            // O esquema e explicito: um access token humano nao serve aqui,
            // nem uma API key serve nas rotas humanas.
            .AddAuthenticationSchemes(ManipuladorDeAutenticacaoDeIntegracao.Esquema)
            .RequireAuthenticatedUser()
            .RequireClaim(ManipuladorDeAutenticacaoDeIntegracao.ClaimDeOrganizacao)
            .RequireClaim(ManipuladorDeAutenticacaoDeIntegracao.ClaimDeIntegracao));
    }

    // -----------------------------------------------------------------------
    // Ingestao (maquina-a-maquina)
    // -----------------------------------------------------------------------

    public static void MapearEndpointsDeIngestao(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);

        var grupo = rotas
            .MapGroup($"{SessaoHttp.PrefixoDaApi}/ingestao")
            .WithTags("Ingestao")
            .RequireAuthorization(PoliticaDeIntegracao)
            .RequireRateLimiting(LimiteDeIngestao);

        grupo.MapPost("/transacoes", async (
                RequisicaoDeIngestao requisicao,
                HttpContext contexto,
                ServicoDeIngestao servico,
                CancellationToken cancellationToken) =>
            {
                var chave = contexto.Request.Headers[CabecalhoDeIdempotencia].ToString();

                var resultado = await servico.RegistrarAsync(
                    new ConteudoDaTransacao(
                        requisicao.IdentificadorExterno,
                        requisicao.Valor,
                        requisicao.Moeda,
                        requisicao.OcorridaEm,
                        requisicao.ClienteExternoId,
                        requisicao.ReferenciaDoInstrumento,
                        requisicao.FingerprintDoDispositivo,
                        requisicao.EnderecoIp,
                        requisicao.PaisDeOrigem),
                    chave,
                    cancellationToken);

                var corpo = RespostaDeIngestao.De(resultado.Transacao, resultado.JaExistia);

                // 201 quando criou agora; 200 quando o pedido era um retry.
                // O integrador precisa dessa diferenca para saber se o retry
                // dele foi necessario.
                return resultado.JaExistia
                    ? Results.Ok(corpo)
                    : Results.Created(
                        $"{SessaoHttp.PrefixoDaApi}/transacoes/{resultado.Transacao.Id}",
                        corpo);
            })
            // Teto do corpo: o contrato inteiro cabe em centenas de bytes.
            // Sem o limite, um corpo de megabytes seria lido inteiro antes de
            // qualquer validacao rodar.
            .WithMetadata(new RequestSizeLimitAttribute(TamanhoMaximoDoCorpo))
            .WithName("IngerirTransacao");
    }

    // -----------------------------------------------------------------------
    // Administracao de integracoes (humano, perfil Administrador)
    // -----------------------------------------------------------------------

    public static void MapearEndpointsDeIntegracoes(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);

        var grupo = rotas
            .MapGroup($"{SessaoHttp.PrefixoDaApi}/integracoes")
            .WithTags("Integracoes")
            .RequireAuthorization(PoliticasDeAutorizacao.Administrador);

        grupo.MapGet("/", async (
                [FromQuery] int? pagina,
                [FromQuery] int? tamanho,
                [FromQuery] string? ordenarPor,
                [FromQuery] string? direcao,
                ServicoDeIntegracoes servico,
                CancellationToken cancellationToken) =>
            {
                var (paginacao, ordenacao) = LerConsulta(
                    pagina,
                    tamanho,
                    ordenarPor,
                    direcao,
                    ServicoDeIntegracoes.CamposDeOrdenacao,
                    ServicoDeIntegracoes.CampoDeOrdenacaoPadrao);

                var resultado = await servico.ListarAsync(paginacao, ordenacao, cancellationToken);

                return Results.Ok(RespostaPaginada.De(resultado, IntegracaoResumida.De));
            })
            .WithName("ListarIntegracoes");

        grupo.MapGet("/{id:guid}", async (
                Guid id,
                ServicoDeIntegracoes servico,
                CancellationToken cancellationToken) =>
            {
                var integracao = await servico.ObterAsync(id, cancellationToken);
                var credenciais = await servico.ListarCredenciaisAsync(id, cancellationToken);

                return Results.Ok(new IntegracaoDetalhada(
                    integracao.Id,
                    integracao.Nome,
                    integracao.Ativa,
                    integracao.CriadaEm,
                    credenciais.Select(CredencialResumida.De).ToList()));
            })
            .WithName("ObterIntegracao");

        grupo.MapPost("/", async (
                RequisicaoDeCriacaoDeIntegracao requisicao,
                ServicoDeIntegracoes servico,
                CancellationToken cancellationToken) =>
            {
                var (integracao, credencial) = await servico.CriarAsync(requisicao.Nome, cancellationToken);

                return Results.Created(
                    $"{SessaoHttp.PrefixoDaApi}/integracoes/{integracao.Id}",
                    new
                    {
                        integracao = IntegracaoResumida.De(integracao),
                        credencial = CredencialEmitidaResposta.De(credencial),
                    });
            })
            .WithName("CriarIntegracao");

        grupo.MapPut("/{id:guid}/nome", async (
                Guid id,
                RequisicaoDeRenomearIntegracao requisicao,
                ServicoDeIntegracoes servico,
                CancellationToken cancellationToken) =>
                Results.Ok(IntegracaoResumida.De(
                    await servico.RenomearAsync(id, requisicao.Nome, cancellationToken))))
            .WithName("RenomearIntegracao");

        grupo.MapPut("/{id:guid}/ativacao", async (
                Guid id,
                RequisicaoDeAtivacaoDeIntegracao requisicao,
                ServicoDeIntegracoes servico,
                CancellationToken cancellationToken) =>
                Results.Ok(IntegracaoResumida.De(
                    await servico.DefinirAtivacaoAsync(id, requisicao.Ativa, cancellationToken))))
            .WithName("DefinirAtivacaoDeIntegracao");

        grupo.MapPost("/{id:guid}/credenciais", async (
                Guid id,
                ServicoDeIntegracoes servico,
                CancellationToken cancellationToken) =>
                Results.Ok(CredencialEmitidaResposta.De(
                    await servico.RotacionarCredencialAsync(id, cancellationToken))))
            .WithName("RotacionarCredencialDeIntegracao");

        grupo.MapDelete("/{id:guid}/credenciais/{credencialId:guid}", async (
                Guid id,
                Guid credencialId,
                ServicoDeIntegracoes servico,
                CancellationToken cancellationToken) =>
            {
                await servico.RevogarCredencialAsync(id, credencialId, cancellationToken);

                return Results.NoContent();
            })
            .WithName("RevogarCredencialDeIntegracao");
    }

    // -----------------------------------------------------------------------
    // Consulta de transacoes (humano)
    // -----------------------------------------------------------------------

    public static void MapearEndpointsDeTransacoes(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);

        // Qualquer perfil autenticado le transacoes do proprio tenant.
        // A tela operacional de verdade chega na Fase 3, com score e sinais.
        var grupo = rotas
            .MapGroup($"{SessaoHttp.PrefixoDaApi}/transacoes")
            .WithTags("Transacoes")
            .RequireAuthorization(PoliticasDeAutorizacao.QualquerPerfil);

        grupo.MapGet("/", async (
                [FromQuery] int? pagina,
                [FromQuery] int? tamanho,
                [FromQuery] string? ordenarPor,
                [FromQuery] string? direcao,
                IRepositorioDeTransacoes transacoes,
                CancellationToken cancellationToken) =>
            {
                var (paginacao, ordenacao) = LerConsulta(
                    pagina,
                    tamanho,
                    ordenarPor,
                    direcao,
                    CamposDeOrdenacaoDeTransacao,
                    "recebidaEm");

                var resultado = await transacoes.ListarAsync(paginacao, ordenacao, cancellationToken);

                return Results.Ok(RespostaPaginada.De(resultado, TransacaoResumida.De));
            })
            .WithName("ListarTransacoes");

        grupo.MapGet("/{id:guid}", async (
                Guid id,
                IRepositorioDeTransacoes transacoes,
                CancellationToken cancellationToken) =>
            {
                var transacao = await transacoes.BuscarPorIdAsync(id, cancellationToken)
                    ?? throw new RecursoNaoEncontrado("Transacao");

                return Results.Ok(TransacaoResumida.De(transacao));
            })
            .WithName("ObterTransacao");
    }

    public static readonly string[] CamposDeOrdenacaoDeTransacao =
        ["recebidaEm", "ocorridaEm", "valor"];

    private static (ParametrosDePaginacao Paginacao, ParametrosDeOrdenacao Ordenacao) LerConsulta(
        int? pagina,
        int? tamanho,
        string? ordenarPor,
        string? direcao,
        string[] camposPermitidos,
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
