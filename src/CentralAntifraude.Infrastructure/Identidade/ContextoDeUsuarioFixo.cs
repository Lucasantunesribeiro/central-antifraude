using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Domain.Identidade;

namespace CentralAntifraude.Infrastructure.Identidade;

/// <summary>
/// Identidade de um contexto sem requisicao HTTP.
///
/// Usada pela fabrica de tempo de design (que so precisa do modelo, nunca
/// consulta dados) e pelo seed, que trabalha dentro de uma organizacao
/// conhecida. Fora desses casos, contexto anonimo significa "nao enxerga
/// nada".
/// </summary>
public sealed class ContextoDeUsuarioFixo : IContextoDoUsuarioAtual
{
    public static readonly ContextoDeUsuarioFixo Anonimo = new(Guid.Empty, Guid.Empty, null);

    public ContextoDeUsuarioFixo(Guid organizacaoId, Guid usuarioId, PerfilDeUsuario? perfil)
    {
        OrganizacaoId = organizacaoId;
        UsuarioId = usuarioId;
        Perfil = perfil;
    }

    public bool EstaAutenticado => OrganizacaoId != Guid.Empty && UsuarioId != Guid.Empty;

    public Guid OrganizacaoId { get; }

    public Guid UsuarioId { get; }

    public PerfilDeUsuario? Perfil { get; }
}
