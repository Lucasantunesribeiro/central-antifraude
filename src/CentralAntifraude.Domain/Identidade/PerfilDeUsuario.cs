namespace CentralAntifraude.Domain.Identidade;

/// <summary>
/// Perfis de acesso da Central Antifraude.
///
/// RBAC fechado: a lista e esta e nao cresce por configuracao. O ROADMAP
/// (secao 1.2) descarta explicitamente um sistema generico de permissoes
/// arbitrarias - ele adicionaria uma superficie de erro grande para resolver
/// um problema que a v1 nao tem.
///
/// Um usuario tem exatamente um perfil dentro da sua organizacao.
/// </summary>
public enum PerfilDeUsuario
{
    /// <summary>Usuarios, permissoes, integracoes e credenciais.</summary>
    Administrador = 1,

    /// <summary>Regras, backtests e supervisao da operacao.</summary>
    SupervisorDeFraude = 2,

    /// <summary>Alertas e investigacao de casos. Usuario operacional principal.</summary>
    AnalistaDeFraude = 3,

    /// <summary>Consulta e trilha de auditoria. Essencialmente leitura.</summary>
    Auditor = 4,
}
