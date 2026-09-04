using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Infrastructure.Identidade;
using CentralAntifraude.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CentralAntifraude.IntegrationTests.Infra;

/// <summary>Uma organizacao e seus usuarios, prontos para o teste usar.</summary>
public sealed record TenantDeTeste(Guid OrganizacaoId, string Codigo)
{
    public Dictionary<PerfilDeUsuario, Guid> UsuariosPorPerfil { get; } = new();

    public string EmailDe(PerfilDeUsuario perfil) => $"{perfil}.{Codigo}@teste.local".ToLowerInvariant();
}

/// <summary>
/// Monta dois tenants completos no banco real e oferece atalhos de login.
///
/// Dois tenants, e nao um: metade do que a Fase 1 promete e que a organizacao
/// A nao alcance nada da B. Isso so pode ser provado com as duas existindo.
/// </summary>
public static class CenarioDeIdentidade
{
    public const string SenhaPadrao = "senha-de-teste-com-tamanho-ok";

    private static readonly PerfilDeUsuario[] Perfis = Enum.GetValues<PerfilDeUsuario>();

    public static async Task<TenantDeTeste> CriarTenantAsync(
        string stringDeConexao,
        string codigo,
        CancellationToken cancellationToken)
    {
        await using var contexto = CriarContexto(stringDeConexao);

        var agora = DateTimeOffset.UtcNow;
        var organizacao = Organizacao.Criar($"Organizacao {codigo}", codigo, agora);
        contexto.Organizacoes.Add(organizacao);

        var tenant = new TenantDeTeste(organizacao.Id, codigo);

        // Mesmo custo reduzido em todos os usuarios de teste: 220.000
        // iteracoes por usuario deixariam a suite lenta sem provar nada a mais.
        var hash = new HashDeSenhaPbkdf2(
            Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));
        var senhaComHash = hash.Gerar(SenhaPadrao);

        foreach (var perfil in Perfis)
        {
            var usuario = Usuario.Criar(
                organizacao.Id,
                Email.De(tenant.EmailDe(perfil)),
                $"{perfil} do {codigo}",
                senhaComHash,
                perfil,
                agora);

            contexto.Usuarios.Add(usuario);
            tenant.UsuariosPorPerfil[perfil] = usuario.Id;
        }

        // Toda organizacao nasce com perfil de risco e catalogo de regras, do
        // mesmo jeito que em producao. Um tenant de teste sem perfil provaria
        // um comportamento que o sistema real nunca tem.
        await ProvisionamentoDeRisco.GarantirCatalogoAsync(
            contexto,
            organizacao.Id,
            agora,
            cancellationToken);

        await contexto.SaveChangesAsync(cancellationToken);

        return tenant;
    }

    public static CentralAntifraudeDbContext CriarContexto(string stringDeConexao)
    {
        var construtor = new DbContextOptionsBuilder<CentralAntifraudeDbContext>();
        OpcoesDoDbContext.Configurar(construtor, stringDeConexao);

        return new CentralAntifraudeDbContext(construtor.Options, ContextoDeUsuarioFixo.Anonimo);
    }

    /// <summary>
    /// Contexto que enxerga como um tenant especifico, para provar o filtro
    /// global do EF Core sem passar pela API.
    /// </summary>
    public static CentralAntifraudeDbContext CriarContextoComoTenant(
        string stringDeConexao,
        Guid organizacaoId)
    {
        var construtor = new DbContextOptionsBuilder<CentralAntifraudeDbContext>();
        OpcoesDoDbContext.Configurar(construtor, stringDeConexao);

        return new CentralAntifraudeDbContext(
            construtor.Options,
            new ContextoDeUsuarioFixo(organizacaoId, Identificador.Novo(), PerfilDeUsuario.Administrador));
    }

    /// <summary>Faz login e devolve o access token e o cookie de sessao.</summary>
    public static async Task<(string AccessToken, string Cookie)> EntrarAsync(
        HttpClient cliente,
        string email,
        CancellationToken cancellationToken,
        string senha = SenhaPadrao)
    {
        var resposta = await cliente.PostAsJsonAsync(
            "/api/auth/login",
            new { email, senha },
            cancellationToken);

        resposta.EnsureSuccessStatusCode();

        var corpo = await resposta.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(corpo);

        var cookie = resposta.Headers.TryGetValues("Set-Cookie", out var valores)
            ? valores.First().Split(';')[0]
            : string.Empty;

        return (json.RootElement.GetProperty("accessToken").GetString()!, cookie);
    }

    public static HttpRequestMessage Autenticada(
        HttpMethod metodo,
        string caminho,
        string accessToken)
    {
        var requisicao = new HttpRequestMessage(metodo, new Uri(caminho, UriKind.Relative));
        requisicao.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            accessToken);

        return requisicao;
    }
}
