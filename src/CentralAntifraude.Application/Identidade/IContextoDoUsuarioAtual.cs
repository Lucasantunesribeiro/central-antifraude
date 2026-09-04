using CentralAntifraude.Domain.Identidade;

namespace CentralAntifraude.Application.Identidade;

/// <summary>
/// Quem esta fazendo a requisicao atual.
///
/// Esta e a unica fonte de autoridade sobre tenant e perfil. O
/// <c>TenantId</c> NUNCA vem do corpo, da query string ou de um cabecalho
/// escolhido pelo cliente (CLAUDE.md secao 9) — ele e derivado da identidade
/// autenticada e mais nada.
///
/// Quando nao ha autenticacao, <see cref="OrganizacaoId"/> vale
/// <see cref="Guid.Empty"/>. Isso e proposital: o filtro de tenant do EF Core
/// compara contra esse valor, entao uma requisicao sem identidade nao enxerga
/// nenhuma linha, em vez de enxergar todas.
/// </summary>
public interface IContextoDoUsuarioAtual
{
    bool EstaAutenticado { get; }

    /// <summary>Tenant efetivo. <see cref="Guid.Empty"/> quando anonimo.</summary>
    Guid OrganizacaoId { get; }

    Guid UsuarioId { get; }

    PerfilDeUsuario? Perfil { get; }
}
