using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Infrastructure.Identidade;
using Microsoft.AspNetCore.Authorization;

namespace CentralAntifraude.Api.Identidade;

/// <summary>
/// RBAC no backend.
///
/// CLAUDE.md secao 52: esconder um botao no frontend nao e autorizacao.
/// Toda rota protegida declara aqui qual perfil pode chama-la, e o frontend
/// apenas reflete essa decisao na navegacao.
///
/// As politicas comparam a claim <c>perfil</c> contra nomes de um enum
/// fechado. Um valor forjado no token nao casa com nenhum deles.
/// </summary>
public static class PoliticasDeAutorizacao
{
    /// <summary>Gestao de usuarios, permissoes, integracoes e credenciais.</summary>
    public const string Administrador = "perfil:administrador";

    /// <summary>Gestao de regras, backtests e supervisao (Fases 8 e 9).</summary>
    public const string Supervisor = "perfil:supervisor";

    /// <summary>Operacao sobre alertas e casos (Fases 6 e 7).</summary>
    public const string OperacaoDeFraude = "perfil:operacao";

    /// <summary>
    /// Leitura da trilha de auditoria (Fase 10).
    ///
    /// Administrador e Auditor, e ninguem mais. A trilha e um controle **sobre**
    /// o que Supervisor e Analista fazem — publicar regra, resolver caso —, e
    /// dar a quem e auditado o poder de varrer o proprio rastro enfraquece o
    /// unico registro que responde "quem fez o que e quando"
    /// (CLAUDE.md secoes 8.3 e 67).
    /// </summary>
    public const string LeituraDeAuditoria = "perfil:auditoria";

    /// <summary>Qualquer perfil autenticado. Leitura de dados da organizacao.</summary>
    public const string QualquerPerfil = "perfil:qualquer";

    public static void Registrar(AuthorizationOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        // Sem politica declarada, a rota exige ao menos autenticacao valida.
        // Falha fechada: uma rota nova que esqueca de declarar autorizacao
        // fica restrita, e nao aberta.
        opcoes.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        Adicionar(opcoes, Administrador, PerfilDeUsuario.Administrador);

        Adicionar(
            opcoes,
            Supervisor,
            PerfilDeUsuario.Administrador,
            PerfilDeUsuario.SupervisorDeFraude);

        // Auditor fica de fora: o perfil e de leitura, e agir sobre alerta ou
        // caso e acao operacional (ROADMAP secao 1.9).
        Adicionar(
            opcoes,
            OperacaoDeFraude,
            PerfilDeUsuario.Administrador,
            PerfilDeUsuario.SupervisorDeFraude,
            PerfilDeUsuario.AnalistaDeFraude);

        Adicionar(
            opcoes,
            LeituraDeAuditoria,
            PerfilDeUsuario.Administrador,
            PerfilDeUsuario.Auditor);

        Adicionar(
            opcoes,
            QualquerPerfil,
            PerfilDeUsuario.Administrador,
            PerfilDeUsuario.SupervisorDeFraude,
            PerfilDeUsuario.AnalistaDeFraude,
            PerfilDeUsuario.Auditor);
    }

    private static void Adicionar(
        AuthorizationOptions opcoes,
        string nome,
        params PerfilDeUsuario[] perfis)
    {
        var aceitos = perfis.Select(p => p.ToString()).ToArray();

        opcoes.AddPolicy(nome, politica => politica
            .RequireAuthenticatedUser()
            .RequireClaim(EmissorDeAccessToken.ClaimDePerfil, aceitos)
            // A claim de organizacao precisa existir: sem ela o filtro de
            // tenant nao tem contra o que comparar, e a requisicao nao deveria
            // ter chegado ate aqui.
            .RequireAssertion(contexto =>
                contexto.User.HasClaim(c => c.Type == EmissorDeAccessToken.ClaimDeOrganizacao)));
    }
}
