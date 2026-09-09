using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Identidade;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CentralAntifraude.Api.Identidade;

public static class EndpointsDeIdentidade
{
    /// <summary>Politica de limite aplicada ao login.</summary>
    public const string LimiteDeLogin = "login";

    /// <summary>
    /// Limite da renovacao de sessao (CLAUDE.md secao 55).
    ///
    /// Generoso, porque renovar e legitimo e frequente — duas abas abertas
    /// renovam em paralelo. Ele existe para o caso oposto: alguem martelando a
    /// rota com cookies sorteados para descobrir um valido.
    /// </summary>
    public const string LimiteDeRefresh = "refresh";

    public static void MapearEndpointsDeAutenticacao(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);

        var grupo = rotas.MapGroup(SessaoHttp.CaminhoDaSessao).WithTags("Autenticacao");

        // ------------------------------------------------------------------
        // Login
        // ------------------------------------------------------------------
        grupo.MapPost("/login", async (
                RequisicaoDeLogin requisicao,
                HttpContext contexto,
                ServicoDeAutenticacao servico,
                OpcoesDeAutenticacao opcoes,
                CancellationToken cancellationToken) =>
            {
                GarantirOrigemConfiavel(contexto, opcoes);

                var sessao = await servico.AutenticarAsync(
                    requisicao.Email,
                    requisicao.Senha,
                    cancellationToken);

                SessaoHttp.EscreverCookieDeSessao(
                    contexto,
                    sessao.RefreshTokenBruto,
                    sessao.RefreshTokenExpiraEm);

                return Results.Ok(new RespostaDeSessao(
                    sessao.AccessToken,
                    sessao.AccessTokenExpiraEm,
                    UsuarioAutenticado.De(sessao.Usuario)));
            })
            .AllowAnonymous()
            // Limite de tentativas: sem ele, a diferenca entre uma senha forte
            // e uma fraca vira apenas tempo de maquina (CLAUDE.md secao 55).
            .RequireRateLimiting(LimiteDeLogin)
            .WithName("Login");

        // ------------------------------------------------------------------
        // Acesso de demonstracao
        // ------------------------------------------------------------------
        grupo.MapPost("/demo", async (
                HttpContext contexto,
                ServicoDeAutenticacao servico,
                OpcoesDeAutenticacao opcoes,
                CancellationToken cancellationToken) =>
            {
                GarantirOrigemConfiavel(contexto, opcoes);

                // Sem corpo e sem e-mail: o servidor escolhe a conta. Com a demo
                // desligada, o servico lanca "recurso inexistente" e a resposta
                // e 404 — o caminho nao se anuncia num ambiente que nao o quer.
                var sessao = await servico.EntrarComoDemoAsync(cancellationToken);

                SessaoHttp.EscreverCookieDeSessao(
                    contexto,
                    sessao.RefreshTokenBruto,
                    sessao.RefreshTokenExpiraEm);

                return Results.Ok(new RespostaDeSessao(
                    sessao.AccessToken,
                    sessao.AccessTokenExpiraEm,
                    UsuarioAutenticado.De(sessao.Usuario)));
            })
            .AllowAnonymous()
            // Limite mais folgado que o do login: nao ha senha a adivinhar aqui,
            // entao o unico risco e criar sessoes demais. O teto contem abuso
            // sem atrapalhar quem so quer ver o produto.
            .RequireRateLimiting(LimiteDeLogin)
            .WithName("AcessoDeDemonstracao");

        // ------------------------------------------------------------------
        // Renovacao
        // ------------------------------------------------------------------
        grupo.MapPost("/refresh", async (
                HttpContext contexto,
                ServicoDeAutenticacao servico,
                OpcoesDeAutenticacao opcoes,
                CancellationToken cancellationToken) =>
            {
                GarantirOrigemConfiavel(contexto, opcoes);

                // O token vem do cookie, jamais do corpo: aceitar pelo corpo
                // permitiria que JavaScript o manipulasse, desfazendo a razao
                // de ele ser HttpOnly.
                var refresh = SessaoHttp.LerCookieDeSessao(contexto);

                try
                {
                    var sessao = await servico.RenovarAsync(refresh, cancellationToken);

                    SessaoHttp.EscreverCookieDeSessao(
                        contexto,
                        sessao.RefreshTokenBruto,
                        sessao.RefreshTokenExpiraEm);

                    return Results.Ok(new RespostaDeSessao(
                        sessao.AccessToken,
                        sessao.AccessTokenExpiraEm,
                        UsuarioAutenticado.De(sessao.Usuario)));
                }
                catch (NaoAutenticado)
                {
                    // Cookie que nao serve mais precisa sair do navegador, ou
                    // o frontend fica tentando renovar com ele para sempre.
                    SessaoHttp.LimparCookieDeSessao(contexto);
                    throw;
                }
            })
            .AllowAnonymous()
            // Renovar e anonimo por natureza — a prova de identidade e o
            // cookie. Sem limite, a rota vira um oraculo para adivinhar
            // valores de refresh token (CLAUDE.md secao 55).
            .RequireRateLimiting(LimiteDeRefresh)
            .WithName("RenovarSessao");

        // ------------------------------------------------------------------
        // Logout
        // ------------------------------------------------------------------
        grupo.MapPost("/logout", async (
                HttpContext contexto,
                ServicoDeAutenticacao servico,
                OpcoesDeAutenticacao opcoes,
                CancellationToken cancellationToken) =>
            {
                GarantirOrigemConfiavel(contexto, opcoes);

                await servico.EncerrarSessaoAsync(
                    SessaoHttp.LerCookieDeSessao(contexto),
                    cancellationToken);

                SessaoHttp.LimparCookieDeSessao(contexto);

                // Sempre 204, mesmo sem sessao. Um erro aqui daria a um
                // atacante uma forma de testar se um cookie e valido.
                return Results.NoContent();
            })
            .AllowAnonymous()
            .WithName("Logout");

        // ------------------------------------------------------------------
        // Sessao atual
        // ------------------------------------------------------------------
        grupo.MapGet("/eu", async (
                IContextoDoUsuarioAtual contextoAtual,
                ServicoDeUsuarios servico,
                CancellationToken cancellationToken) =>
            {
                var usuario = await servico.ObterAsync(contextoAtual.UsuarioId, cancellationToken);

                return Results.Ok(UsuarioAutenticado.De(usuario));
            })
            .RequireAuthorization(PoliticasDeAutorizacao.QualquerPerfil)
            .WithName("SessaoAtual");
    }

    public static void MapearEndpointsDeUsuarios(this IEndpointRouteBuilder rotas)
    {
        ArgumentNullException.ThrowIfNull(rotas);

        // Gestao de usuarios e exclusiva do Administrador. Declarado no grupo
        // inteiro para que nenhuma rota nova aqui dentro escape por omissao.
        var grupo = rotas
            .MapGroup($"{SessaoHttp.PrefixoDaApi}/usuarios")
            .WithTags("Usuarios")
            .RequireAuthorization(PoliticasDeAutorizacao.Administrador);

        grupo.MapGet("/", async (
                [FromQuery] int? pagina,
                [FromQuery] int? tamanho,
                [FromQuery] string? ordenarPor,
                [FromQuery] string? direcao,
                ServicoDeUsuarios servico,
                CancellationToken cancellationToken) =>
            {
                if (!ParametrosDePaginacao.TentarCriar(pagina, tamanho, out var paginacao, out var erroPaginacao))
                {
                    throw new ErroDeValidacao("paginacao", erroPaginacao);
                }

                if (!ParametrosDeOrdenacao.TentarCriar(
                        ordenarPor,
                        direcao,
                        ServicoDeUsuarios.CamposDeOrdenacao,
                        ServicoDeUsuarios.CampoDeOrdenacaoPadrao,
                        out var ordenacao,
                        out var erroOrdenacao))
                {
                    throw new ErroDeValidacao("ordenacao", erroOrdenacao);
                }

                var resultado = await servico.ListarAsync(paginacao, ordenacao, cancellationToken);

                return Results.Ok(RespostaPaginada.De(resultado, UsuarioResumido.De));
            })
            .WithName("ListarUsuarios");

        grupo.MapGet("/{id:guid}", async (
                Guid id,
                ServicoDeUsuarios servico,
                CancellationToken cancellationToken) =>
                Results.Ok(UsuarioResumido.De(await servico.ObterAsync(id, cancellationToken))))
            .WithName("ObterUsuario");

        grupo.MapPost("/", async (
                RequisicaoDeCriacaoDeUsuario requisicao,
                ServicoDeUsuarios servico,
                CancellationToken cancellationToken) =>
            {
                var usuario = await servico.CriarAsync(
                    requisicao.Email,
                    requisicao.NomeCompleto,
                    requisicao.Senha,
                    requisicao.Perfil,
                    cancellationToken);

                return Results.Created(
                    $"{SessaoHttp.PrefixoDaApi}/usuarios/{usuario.Id}",
                    UsuarioResumido.De(usuario));
            })
            .WithName("CriarUsuario");

        grupo.MapPut("/{id:guid}/perfil", async (
                Guid id,
                RequisicaoDeAlteracaoDePerfil requisicao,
                ServicoDeUsuarios servico,
                CancellationToken cancellationToken) =>
                Results.Ok(UsuarioResumido.De(
                    await servico.AlterarPerfilAsync(id, requisicao.Perfil, cancellationToken))))
            .WithName("AlterarPerfilDeUsuario");

        grupo.MapPut("/{id:guid}/nome", async (
                Guid id,
                RequisicaoDeAlteracaoDeNome requisicao,
                ServicoDeUsuarios servico,
                CancellationToken cancellationToken) =>
                Results.Ok(UsuarioResumido.De(
                    await servico.AlterarNomeAsync(id, requisicao.NomeCompleto, cancellationToken))))
            .WithName("AlterarNomeDeUsuario");

        grupo.MapPut("/{id:guid}/ativacao", async (
                Guid id,
                RequisicaoDeAtivacao requisicao,
                ServicoDeUsuarios servico,
                CancellationToken cancellationToken) =>
                Results.Ok(UsuarioResumido.De(
                    await servico.DefinirAtivacaoAsync(id, requisicao.Ativo, cancellationToken))))
            .WithName("DefinirAtivacaoDeUsuario");

        grupo.MapPut("/{id:guid}/senha", async (
                Guid id,
                RequisicaoDeDefinicaoDeSenha requisicao,
                ServicoDeUsuarios servico,
                CancellationToken cancellationToken) =>
            {
                await servico.DefinirSenhaAsync(id, requisicao.Senha, cancellationToken);

                return Results.NoContent();
            })
            .WithName("DefinirSenhaDeUsuario");
    }

    private static void GarantirOrigemConfiavel(HttpContext contexto, OpcoesDeAutenticacao opcoes)
    {
        if (!SessaoHttp.OrigemEhConfiavel(contexto, opcoes))
        {
            throw new NaoAutorizado("Origem da requisicao nao permitida.");
        }
    }
}
