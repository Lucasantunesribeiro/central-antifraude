using CentralAntifraude.Domain.Risco;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.Infrastructure.Persistencia;

/// <summary>
/// Garante que uma organizacao tenha perfil de risco e regras.
///
/// Uma organizacao sem perfil ativo nao consegue avaliar transacao nenhuma, e
/// descobrir isso na primeira ingestao — em producao, com o integrador
/// esperando resposta — seria tarde. Por isso o catalogo padrao nasce junto da
/// organizacao.
///
/// Roda FORA de uma requisicao (seed, criacao de tenant em teste), quando nao
/// ha identidade e o filtro global de tenant devolveria vazio para tudo. Por
/// isso as consultas daqui desligam o filtro explicitamente e filtram pela
/// organizacao a mao — que e justamente o que o filtro faria se houvesse
/// contexto.
/// </summary>
public static class ProvisionamentoDeRisco
{
    /// <summary>
    /// Provisiona o catalogo padrao se a organizacao ainda nao tiver perfil.
    ///
    /// Idempotente: rodar de novo nao cria um segundo perfil nem uma segunda
    /// versao. Devolve <c>true</c> quando provisionou agora.
    ///
    /// Nao chama <c>SaveChanges</c>: quem chama decide o momento da gravacao,
    /// porque a criacao da organizacao e a do catalogo devem entrar na mesma
    /// transacao.
    /// </summary>
    public static async Task<bool> GarantirCatalogoAsync(
        CentralAntifraudeDbContext contexto,
        Guid organizacaoId,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contexto);

        var jaTem = await contexto.PerfisDeRisco
            .IgnoreQueryFilters([CentralAntifraudeDbContext.FiltroDeTenant])
            .AnyAsync(p => p.OrganizacaoId == organizacaoId, cancellationToken);

        if (jaTem)
        {
            return false;
        }

        var catalogo = CatalogoPadraoDeRisco.Provisionar(organizacaoId, agora);

        contexto.PerfisDeRisco.Add(catalogo.Perfil);
        contexto.Regras.AddRange(catalogo.Regras);
        contexto.VersoesDeRegra.AddRange(catalogo.VersoesDeRegra);
        contexto.VersoesDePerfilDeRisco.Add(catalogo.VersaoDoPerfil);

        return true;
    }
}
