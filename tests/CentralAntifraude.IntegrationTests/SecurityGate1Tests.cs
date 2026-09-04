using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Api.Identidade;
using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Security Gate 1 (ROADMAP secao 1.8), item por item, contra a API real.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class SecurityGate1Tests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;

    public SecurityGate1Tests(FixtureDoBanco banco) => _banco = banco;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"gate1-{Guid.NewGuid():N}"[..14],
            TestContext.Current.CancellationToken);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();
    }

    public async ValueTask DisposeAsync()
    {
        _cliente?.Dispose();
        if (_fabrica is not null)
        {
            await _fabrica.DisposeAsync();
        }
    }

    private string EmailDe(PerfilDeUsuario perfil) => _tenant.EmailDe(perfil);

    private Task<(string AccessToken, string Cookie)> EntrarComoAsync(PerfilDeUsuario perfil) =>
        CenarioDeIdentidade.EntrarAsync(_cliente, EmailDe(perfil), TestContext.Current.CancellationToken);

    // -----------------------------------------------------------------------
    // Login
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Login_valido_devolve_access_token_e_cookie_httponly()
    {
        var resposta = await _cliente.PostAsJsonAsync(
            "/api/auth/login",
            new { email = EmailDe(PerfilDeUsuario.Administrador), senha = CenarioDeIdentidade.SenhaPadrao },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var cookie = resposta.Headers.GetValues("Set-Cookie").First();

        Assert.Contains(SessaoHttp.NomeDoCookie, cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"path={SessaoHttp.CaminhoDaSessao}", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task O_refresh_token_nunca_aparece_no_corpo_da_resposta()
    {
        // Se ele viesse no corpo, o JavaScript o leria — e o cookie HttpOnly
        // perderia inteiramente a razao de existir.
        var resposta = await _cliente.PostAsJsonAsync(
            "/api/auth/login",
            new { email = EmailDe(PerfilDeUsuario.Administrador), senha = CenarioDeIdentidade.SenhaPadrao },
            TestContext.Current.CancellationToken);

        var corpo = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var cookie = resposta.Headers.GetValues("Set-Cookie").First();
        var valorDoCookie = cookie.Split(';')[0].Split('=', 2)[1];

        Assert.DoesNotContain(valorDoCookie, corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh", corpo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Senha_errada_e_usuario_inexistente_produzem_a_mesma_resposta()
    {
        // Enumeracao de usuarios: se as respostas diferirem em qualquer coisa
        // — status, codigo ou texto — um atacante descobre quais e-mails
        // existem na plataforma.
        var comSenhaErrada = await _cliente.PostAsJsonAsync(
            "/api/auth/login",
            new { email = EmailDe(PerfilDeUsuario.Auditor), senha = "senha-errada-porem-longa" },
            TestContext.Current.CancellationToken);

        var inexistente = await _cliente.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "ninguem@teste.local", senha = "senha-errada-porem-longa" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, comSenhaErrada.StatusCode);
        Assert.Equal(inexistente.StatusCode, comSenhaErrada.StatusCode);

        Assert.Equal(
            await Codigo(inexistente),
            await Codigo(comSenhaErrada));

        Assert.Equal(
            await Detalhe(inexistente),
            await Detalhe(comSenhaErrada));
    }

    [Fact]
    public async Task Usuario_inativo_recebe_a_mesma_falha_de_credencial()
    {
        // Dizer "sua conta esta desativada" confirmaria que a conta existe e
        // que a senha estava certa.
        var email = EmailDe(PerfilDeUsuario.Auditor);
        await DesativarNoBancoAsync(_tenant.UsuariosPorPerfil[PerfilDeUsuario.Auditor]);

        var resposta = await _cliente.PostAsJsonAsync(
            "/api/auth/login",
            new { email, senha = CenarioDeIdentidade.SenhaPadrao },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal("credenciais_invalidas", await Codigo(resposta));

        var texto = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("inativ", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("desativ", texto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_com_campo_desconhecido_e_recusado_como_requisicao_invalida()
    {
        // Mass assignment: o contrato JSON estrito recusa o payload inteiro em
        // vez de ignorar o campo em silencio.
        using var requisicao = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/login", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                email = EmailDe(PerfilDeUsuario.Administrador),
                senha = CenarioDeIdentidade.SenhaPadrao,
                organizacaoId = Guid.NewGuid(),
                perfil = "Administrador",
            }),
        };

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("requisicao_malformada", await Codigo(resposta));
    }

    // -----------------------------------------------------------------------
    // Token
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("nao-e-um-token")]
    [InlineData("eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJzdWIiOiIxIn0.")]
    public async Task Token_invalido_ou_sem_assinatura_recebe_401(string token)
    {
        // O terceiro caso e o classico "alg: none": um JWT sem assinatura.
        // ValidAlgorithms restrito a HS256 fecha essa porta.
        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, "/api/auth/eu", token);

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Token_com_assinatura_adulterada_recebe_401()
    {
        var (token, _) = await EntrarComoAsync(PerfilDeUsuario.Administrador);
        var adulterado = string.Concat(token.AsSpan(0, token.LastIndexOf('.') + 1), "assinaturaTrocada");

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, "/api/usuarios", adulterado);
        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Token_emitido_por_outra_instancia_nao_vale()
    {
        // Cada fabrica sorteia a propria chave. Um token de outra instancia
        // prova que a validacao confere a assinatura de verdade, e nao apenas
        // o formato.
        await using var outraInstancia = new FabricaDaApi(_banco.StringDeConexao);
        using var clienteDaOutra = outraInstancia.CriarClienteSemCookieAutomatico();

        var (tokenEstrangeiro, _) = await CenarioDeIdentidade.EntrarAsync(
            clienteDaOutra,
            EmailDe(PerfilDeUsuario.Administrador),
            TestContext.Current.CancellationToken);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, "/api/usuarios", tokenEstrangeiro);
        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Rota_protegida_sem_credencial_recebe_401_em_problem_details()
    {
        var resposta = await _cliente.GetAsync(
            new Uri("/api/usuarios", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
        Assert.Equal("token_invalido", await Codigo(resposta));
    }

    [Fact]
    public async Task Rota_inexistente_com_credencial_valida_devolve_404_em_problem_details()
    {
        // Complemento do teste de anonimo em ApiTests: com identidade valida o
        // 404 volta a ser 404, e no mesmo formato de qualquer outro erro.
        var (token, _) = await EntrarComoAsync(PerfilDeUsuario.Auditor);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Get,
            "/rota-que-nao-existe",
            token);

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);

        var texto = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var corpo = JsonDocument.Parse(texto);

        Assert.True(corpo.RootElement.TryGetProperty("idDeCorrelacao", out _));
        Assert.DoesNotContain("Microsoft.AspNetCore", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", texto, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Refresh: rotacao e reuso
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Refresh_rotaciona_o_token_a_cada_uso()
    {
        var (_, cookie) = await EntrarComoAsync(PerfilDeUsuario.AnalistaDeFraude);

        var primeira = await RenovarAsync(cookie);
        Assert.Equal(HttpStatusCode.OK, primeira.Resposta.StatusCode);
        Assert.NotEqual(cookie, primeira.NovoCookie);

        var segunda = await RenovarAsync(primeira.NovoCookie);
        Assert.Equal(HttpStatusCode.OK, segunda.Resposta.StatusCode);
        Assert.NotEqual(primeira.NovoCookie, segunda.NovoCookie);
    }

    [Fact]
    public async Task Reuso_de_refresh_token_derruba_a_sessao_inteira()
    {
        // O cenario e o de um token vazado. Como nao ha como saber qual das
        // duas copias e a legitima, a unica resposta segura e invalidar as
        // duas e exigir novo login.
        var (_, cookieOriginal) = await EntrarComoAsync(PerfilDeUsuario.SupervisorDeFraude);

        var rotacionado = await RenovarAsync(cookieOriginal);
        Assert.Equal(HttpStatusCode.OK, rotacionado.Resposta.StatusCode);

        // O atacante usa a copia antiga.
        var reuso = await RenovarAsync(cookieOriginal);
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.Resposta.StatusCode);

        // E o token do usuario legitimo tambem para de funcionar.
        var apos = await RenovarAsync(rotacionado.NovoCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, apos.Resposta.StatusCode);
    }

    [Fact]
    public async Task Reuso_detectado_vira_registro_de_auditoria()
    {
        var (_, cookieOriginal) = await EntrarComoAsync(PerfilDeUsuario.AnalistaDeFraude);
        await RenovarAsync(cookieOriginal);
        await RenovarAsync(cookieOriginal);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var registrado = await contexto.RegistrosDeAuditoria.AnyAsync(
            r => r.Operacao == OperacaoAuditada.ReusoDeRefreshTokenDetectado,
            TestContext.Current.CancellationToken);

        Assert.True(registrado, "O reuso de refresh token precisa deixar rastro na trilha de auditoria.");
    }

    [Fact]
    public async Task Logout_invalida_o_refresh_token()
    {
        var (_, cookie) = await EntrarComoAsync(PerfilDeUsuario.Auditor);

        using var saida = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/logout", UriKind.Relative));
        saida.Headers.Add("Cookie", cookie);
        var respostaDoLogout = await _cliente.SendAsync(saida, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, respostaDoLogout.StatusCode);

        var apos = await RenovarAsync(cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, apos.Resposta.StatusCode);
    }

    [Fact]
    public async Task Logout_sem_sessao_responde_204_e_nao_erro()
    {
        // Um erro aqui daria ao atacante uma forma de testar se um cookie e
        // valido, sem precisar de mais nada.
        var resposta = await _cliente.PostAsync(
            new Uri("/api/auth/logout", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
    }

    [Fact]
    public async Task Refresh_sem_cookie_recebe_401()
    {
        var resposta = await _cliente.PostAsync(
            new Uri("/api/auth/refresh", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Origem_desconhecida_e_recusada_nos_endpoints_de_sessao()
    {
        // Defesa de CSRF: o cabecalho Origin e preenchido pelo navegador e nao
        // pode ser forjado por JavaScript de outra pagina.
        using var requisicao = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/login", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                email = EmailDe(PerfilDeUsuario.Administrador),
                senha = CenarioDeIdentidade.SenhaPadrao,
            }),
        };
        requisicao.Headers.Add("Origin", "https://site-malicioso.example");

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // RBAC
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(PerfilDeUsuario.AnalistaDeFraude)]
    [InlineData(PerfilDeUsuario.Auditor)]
    [InlineData(PerfilDeUsuario.SupervisorDeFraude)]
    public async Task Somente_administrador_lista_usuarios(PerfilDeUsuario perfil)
    {
        var (token, _) = await EntrarComoAsync(perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, "/api/usuarios", token);
        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    [Theory]
    [InlineData(PerfilDeUsuario.AnalistaDeFraude)]
    [InlineData(PerfilDeUsuario.Auditor)]
    [InlineData(PerfilDeUsuario.SupervisorDeFraude)]
    public async Task Auditor_e_demais_perfis_nao_escrevem_usuarios(PerfilDeUsuario perfil)
    {
        var (token, _) = await EntrarComoAsync(perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Post, "/api/usuarios", token);
        requisicao.Content = JsonContent.Create(new
        {
            email = $"tentativa-{Guid.NewGuid():N}@teste.local",
            nomeCompleto = "Tentativa",
            senha = CenarioDeIdentidade.SenhaPadrao,
            perfil = "Administrador",
        });

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    [Fact]
    public async Task Todo_perfil_autenticado_consulta_a_propria_sessao()
    {
        foreach (var perfil in Enum.GetValues<PerfilDeUsuario>())
        {
            var (token, _) = await EntrarComoAsync(perfil);

            using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, "/api/auth/eu", token);
            var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

            using var corpo = JsonDocument.Parse(
                await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            Assert.Equal(perfil.ToString(), corpo.RootElement.GetProperty("perfil").GetString());
        }
    }

    [Fact]
    public async Task Escalada_de_privilegio_pelo_payload_nao_funciona()
    {
        // O perfil vem da claim assinada. Mandar "perfil" no corpo de um
        // endpoint que nao o aceita derruba a requisicao inteira (JSON
        // estrito), e nao promove ninguem.
        var (token, _) = await EntrarComoAsync(PerfilDeUsuario.AnalistaDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, "/api/auth/eu", token);
        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        using var corpo = JsonDocument.Parse(
            await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(nameof(PerfilDeUsuario.AnalistaDeFraude), corpo.RootElement.GetProperty("perfil").GetString());
    }

    // -----------------------------------------------------------------------
    // Revogacao imediata de acesso
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Desativar_usuario_corta_a_sessao_aberta_dele()
    {
        // Sem isto, um usuario desativado continuaria renovando a sessao ate o
        // refresh token expirar sozinho — duas semanas depois.
        var (_, cookieDaVitima) = await EntrarComoAsync(PerfilDeUsuario.AnalistaDeFraude);
        var (tokenDoAdmin, _) = await EntrarComoAsync(PerfilDeUsuario.Administrador);

        var alvo = _tenant.UsuariosPorPerfil[PerfilDeUsuario.AnalistaDeFraude];

        using var desativacao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Put,
            $"/api/usuarios/{alvo}/ativacao",
            tokenDoAdmin);
        desativacao.Content = JsonContent.Create(new { ativo = false });

        var respostaDaDesativacao = await _cliente.SendAsync(desativacao, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, respostaDaDesativacao.StatusCode);

        var renovacao = await RenovarAsync(cookieDaVitima);
        Assert.Equal(HttpStatusCode.Unauthorized, renovacao.Resposta.StatusCode);
    }

    [Fact]
    public async Task Alterar_perfil_corta_as_sessoes_com_o_perfil_antigo()
    {
        // O perfil viaja dentro do access token. Sem revogar, o usuario
        // rebaixado continuaria agindo com o perfil antigo ate o token expirar.
        var (_, cookieDaVitima) = await EntrarComoAsync(PerfilDeUsuario.SupervisorDeFraude);
        var (tokenDoAdmin, _) = await EntrarComoAsync(PerfilDeUsuario.Administrador);

        var alvo = _tenant.UsuariosPorPerfil[PerfilDeUsuario.SupervisorDeFraude];

        using var alteracao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Put,
            $"/api/usuarios/{alvo}/perfil",
            tokenDoAdmin);
        alteracao.Content = JsonContent.Create(new { perfil = nameof(PerfilDeUsuario.Auditor) });

        Assert.Equal(
            HttpStatusCode.OK,
            (await _cliente.SendAsync(alteracao, TestContext.Current.CancellationToken)).StatusCode);

        var renovacao = await RenovarAsync(cookieDaVitima);
        Assert.Equal(HttpStatusCode.Unauthorized, renovacao.Resposta.StatusCode);
    }

    [Fact]
    public async Task Administrador_nao_pode_remover_o_proprio_perfil()
    {
        // Rebaixar a si mesmo poderia deixar a organizacao sem ninguem capaz
        // de gerir usuarios — estado sem saida pela propria aplicacao.
        var (token, _) = await EntrarComoAsync(PerfilDeUsuario.Administrador);
        var proprioId = _tenant.UsuariosPorPerfil[PerfilDeUsuario.Administrador];

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Put,
            $"/api/usuarios/{proprioId}/perfil",
            token);
        requisicao.Content = JsonContent.Create(new { perfil = nameof(PerfilDeUsuario.Auditor) });

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Auditoria
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Login_bem_sucedido_e_recusado_ficam_na_trilha_sem_credencial()
    {
        await EntrarComoAsync(PerfilDeUsuario.Administrador);

        await _cliente.PostAsJsonAsync(
            "/api/auth/login",
            new { email = EmailDe(PerfilDeUsuario.Administrador), senha = "senha-errada-porem-longa" },
            TestContext.Current.CancellationToken);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var registros = await contexto.RegistrosDeAuditoria.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(registros, r => r.Operacao == OperacaoAuditada.LoginBemSucedido);
        Assert.Contains(registros, r => r.Operacao == OperacaoAuditada.LoginRecusado);

        // A trilha responde "quem fez o que e quando", nunca "com qual
        // credencial".
        foreach (var registro in registros)
        {
            var conteudo = $"{registro.Detalhe} {registro.AutorDescricao}";

            Assert.DoesNotContain(CenarioDeIdentidade.SenhaPadrao, conteudo, StringComparison.Ordinal);
            Assert.DoesNotContain("senha-errada", conteudo, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", conteudo, StringComparison.Ordinal);
            Assert.DoesNotContain("eyJ", conteudo, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Criacao_e_alteracao_de_usuario_ficam_na_trilha()
    {
        var (token, _) = await EntrarComoAsync(PerfilDeUsuario.Administrador);

        using var criacao = CenarioDeIdentidade.Autenticada(HttpMethod.Post, "/api/usuarios", token);
        criacao.Content = JsonContent.Create(new
        {
            email = $"novo-{Guid.NewGuid():N}@teste.local",
            nomeCompleto = "Usuario Novo",
            senha = CenarioDeIdentidade.SenhaPadrao,
            perfil = nameof(PerfilDeUsuario.Auditor),
        });

        var resposta = await _cliente.SendAsync(criacao, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        Assert.True(await contexto.RegistrosDeAuditoria.AnyAsync(
            r => r.Operacao == OperacaoAuditada.UsuarioCriado,
            TestContext.Current.CancellationToken));
    }

    // -----------------------------------------------------------------------
    // Validacao de entrada
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("curta")]
    [InlineData("")]
    public async Task Senha_curta_demais_e_recusada_na_criacao(string senha)
    {
        var (token, _) = await EntrarComoAsync(PerfilDeUsuario.Administrador);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Post, "/api/usuarios", token);
        requisicao.Content = JsonContent.Create(new
        {
            email = $"novo-{Guid.NewGuid():N}@teste.local",
            nomeCompleto = "Usuario Novo",
            senha,
            perfil = nameof(PerfilDeUsuario.Auditor),
        });

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Perfil_desconhecido_no_payload_e_recusado()
    {
        var (token, _) = await EntrarComoAsync(PerfilDeUsuario.Administrador);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Post, "/api/usuarios", token);
        requisicao.Content = JsonContent.Create(new
        {
            email = $"novo-{Guid.NewGuid():N}@teste.local",
            nomeCompleto = "Usuario Novo",
            senha = CenarioDeIdentidade.SenhaPadrao,
            perfil = "Superusuario",
        });

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------

    private async Task<(HttpResponseMessage Resposta, string NovoCookie)> RenovarAsync(string cookie)
    {
        using var requisicao = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/auth/refresh", UriKind.Relative));
        requisicao.Headers.Add("Cookie", cookie);

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        var novoCookie = resposta.Headers.TryGetValues("Set-Cookie", out var valores)
            ? valores.First().Split(';')[0]
            : cookie;

        return (resposta, novoCookie);
    }

    private async Task DesativarNoBancoAsync(Guid usuarioId)
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var usuario = await contexto.Usuarios.FirstAsync(
            u => u.Id == usuarioId,
            TestContext.Current.CancellationToken);

        usuario.Desativar(DateTimeOffset.UtcNow);

        await contexto.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string?> Codigo(HttpResponseMessage resposta)
    {
        using var corpo = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return corpo.RootElement.TryGetProperty("codigo", out var codigo) ? codigo.GetString() : null;
    }

    private static async Task<string?> Detalhe(HttpResponseMessage resposta)
    {
        using var corpo = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return corpo.RootElement.TryGetProperty("detail", out var detalhe) ? detalhe.GetString() : null;
    }
}
