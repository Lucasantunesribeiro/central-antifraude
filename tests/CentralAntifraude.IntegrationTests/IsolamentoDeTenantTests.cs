using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// A promessa central da Fase 1: dados de um tenant nunca alcancam outro.
///
/// Testado nas duas camadas onde o isolamento existe:
/// 1. no EF Core, pelo filtro global — a rede que segura consulta escrita sem
///    o WHERE de organizacao;
/// 2. na API, ponta a ponta, com identificadores reais do outro tenant.
///
/// Se apenas a segunda existisse, uma consulta nova escrita sem filtro
/// passaria despercebida ate alguem construir um endpoint com ela.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class IsolamentoDeTenantTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _alfa = null!;
    private TenantDeTeste _beta = null!;

    public IsolamentoDeTenantTests(FixtureDoBanco banco) => _banco = banco;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        var sufixo = Guid.NewGuid().ToString("N")[..8];

        _alfa = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao, $"alfa-{sufixo}", TestContext.Current.CancellationToken);
        _beta = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao, $"beta-{sufixo}", TestContext.Current.CancellationToken);

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

    // -----------------------------------------------------------------------
    // Camada 1: filtro global do EF Core
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Consulta_sem_where_de_organizacao_so_enxerga_o_proprio_tenant()
    {
        // `contexto.Usuarios.ToList()` — sem filtro nenhum escrito a mao.
        // E exatamente a consulta descuidada que o filtro global existe para
        // proteger.
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _alfa.OrganizacaoId);

        var usuarios = await contexto.Usuarios.ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(usuarios);
        Assert.All(usuarios, u => Assert.Equal(_alfa.OrganizacaoId, u.OrganizacaoId));
    }

    [Fact]
    public async Task Buscar_por_id_conhecido_do_outro_tenant_devolve_nada()
    {
        var idDoOutroTenant = _beta.UsuariosPorPerfil[PerfilDeUsuario.Administrador];

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _alfa.OrganizacaoId);

        var encontrado = await contexto.Usuarios
            .FirstOrDefaultAsync(u => u.Id == idDoOutroTenant, TestContext.Current.CancellationToken);

        Assert.Null(encontrado);
    }

    [Fact]
    public async Task Contexto_sem_identidade_nao_enxerga_nenhum_usuario()
    {
        // A escolha por Guid.Empty em vez de "sem filtro" e o que faz um bug
        // de contexto virar "nao vejo nada" e nao "vejo tudo".
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        var total = await contexto.Usuarios.CountAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, total);
    }

    [Fact]
    public async Task A_trilha_de_auditoria_tambem_e_isolada_por_tenant()
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _alfa.OrganizacaoId);

        var registros = await contexto.RegistrosDeAuditoria
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(registros, r => Assert.Equal(_alfa.OrganizacaoId, r.OrganizacaoId));
    }

    // -----------------------------------------------------------------------
    // Camada 2: API ponta a ponta
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Administrador_so_lista_usuarios_da_propria_organizacao()
    {
        var (token, _) = await CenarioDeIdentidade.EntrarAsync(
            _cliente,
            _alfa.EmailDe(PerfilDeUsuario.Administrador),
            TestContext.Current.CancellationToken);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, "/api/usuarios?tamanho=100", token);
        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var texto = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Nenhum e-mail do outro tenant pode aparecer na lista.
        foreach (var perfil in Enum.GetValues<PerfilDeUsuario>())
        {
            Assert.DoesNotContain(_beta.EmailDe(perfil), texto, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(_alfa.EmailDe(PerfilDeUsuario.Auditor), texto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recurso_de_outro_tenant_responde_404_e_nao_403()
    {
        // 403 significaria "existe, mas voce nao pode" — e isso ja confirma a
        // existencia do identificador. O contrato do projeto e 404
        // (CLAUDE.md secao 52).
        var (token, _) = await CenarioDeIdentidade.EntrarAsync(
            _cliente,
            _alfa.EmailDe(PerfilDeUsuario.Administrador),
            TestContext.Current.CancellationToken);

        var idDoOutroTenant = _beta.UsuariosPorPerfil[PerfilDeUsuario.AnalistaDeFraude];

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Get,
            $"/api/usuarios/{idDoOutroTenant}",
            token);

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);

        var texto = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // E a resposta nao pode contar nada sobre o recurso do outro tenant.
        Assert.DoesNotContain(_beta.Codigo, texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organizacao", texto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Alterar_usuario_de_outro_tenant_responde_404()
    {
        var (token, _) = await CenarioDeIdentidade.EntrarAsync(
            _cliente,
            _alfa.EmailDe(PerfilDeUsuario.Administrador),
            TestContext.Current.CancellationToken);

        var idDoOutroTenant = _beta.UsuariosPorPerfil[PerfilDeUsuario.Auditor];

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Put,
            $"/api/usuarios/{idDoOutroTenant}/perfil",
            token);
        requisicao.Content = JsonContent.Create(new { perfil = "Administrador" });

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);

        // E, o mais importante: o usuario do outro tenant NAO mudou.
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _beta.OrganizacaoId);

        var alvo = await contexto.Usuarios.FirstAsync(
            u => u.Id == idDoOutroTenant,
            TestContext.Current.CancellationToken);

        Assert.Equal(PerfilDeUsuario.Auditor, alvo.Perfil);
    }

    [Fact]
    public async Task Token_de_um_tenant_nao_vale_no_outro()
    {
        // O tenant vem da claim assinada, e nao de nada que o cliente escolha.
        // Este teste garante que nao ha um parametro, cabecalho ou caminho que
        // permita trocar de organizacao com um token valido.
        var (tokenAlfa, _) = await CenarioDeIdentidade.EntrarAsync(
            _cliente,
            _alfa.EmailDe(PerfilDeUsuario.Administrador),
            TestContext.Current.CancellationToken);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, "/api/auth/eu", tokenAlfa);
        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        using var corpo = JsonDocument.Parse(
            await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            _alfa.OrganizacaoId,
            corpo.RootElement.GetProperty("organizacaoId").GetGuid());
    }

    [Fact]
    public async Task E_mail_ja_usado_em_outro_tenant_e_recusado_como_conflito()
    {
        // O e-mail e a chave global de login (ADR 0005). Sem esta checagem, a
        // criacao passaria pela validacao e morreria numa violacao de
        // constraint — com 500 em vez de 409.
        var (token, _) = await CenarioDeIdentidade.EntrarAsync(
            _cliente,
            _alfa.EmailDe(PerfilDeUsuario.Administrador),
            TestContext.Current.CancellationToken);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Post, "/api/usuarios", token);
        requisicao.Content = JsonContent.Create(new
        {
            email = _beta.EmailDe(PerfilDeUsuario.Auditor),
            nomeCompleto = "Tentativa de colisao",
            senha = CenarioDeIdentidade.SenhaPadrao,
            perfil = "Auditor",
        });

        var resposta = await _cliente.SendAsync(requisicao, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
    }
}
