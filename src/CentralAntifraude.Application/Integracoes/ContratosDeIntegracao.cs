using CentralAntifraude.Application.Comum;
using CentralAntifraude.Domain.Integracoes;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Application.Integracoes;

/// <summary>
/// Quem esta enviando a requisicao de ingestao atual.
///
/// O espelho maquina-a-maquina de <c>IContextoDoUsuarioAtual</c>, e pela mesma
/// razao: o tenant vem da credencial autenticada e de mais nada. Um campo
/// <c>tenantId</c> no corpo e apenas um campo desconhecido — recusado pelo
/// contrato JSON estrito.
/// </summary>
public interface IContextoDaIntegracaoAtual
{
    bool EstaAutenticada { get; }

    Guid OrganizacaoId { get; }

    Guid IntegracaoId { get; }
}

/// <summary>Credencial recem-criada. O valor bruto so existe aqui.</summary>
public sealed record CredencialEmitida(CredencialDeIntegracao Credencial, string ValorBruto);

/// <summary>
/// Geracao e conferencia de credencial de integracao.
///
/// Mesmo raciocinio do refresh token (ADR 0006): o segredo tem 256 bits
/// sorteados, entao SHA-256 basta. Alongamento de chave existe para compensar
/// baixa entropia de senha humana, e nao ha senha humana aqui.
/// </summary>
public interface IProtetorDeCredencial
{
    /// <summary>Gera o par (identificador publico, segredo) e o valor bruto completo.</summary>
    (string IdentificadorPublico, string HashDoSegredo, string ValorBruto) Gerar();

    /// <summary>
    /// Separa uma chave apresentada em suas partes. Devolve falso para
    /// qualquer coisa fora do formato — sem lancar excecao, porque texto
    /// malformado em cabecalho de autenticacao e rotina, nao defeito.
    /// </summary>
    bool TentarInterpretar(string? valorBruto, out string identificadorPublico, out string hashDoSegredo);
}

/// <summary>Deriva o fingerprint de IP. O endereco em si nunca e persistido.</summary>
public interface IFingerprintDeIp
{
    string? Derivar(string? enderecoIp);
}

public interface IRepositorioDeIntegracoes
{
    Task<Integracao?> BuscarPorIdAsync(Guid integracaoId, CancellationToken cancellationToken);

    Task<Pagina<Integracao>> ListarAsync(
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CredencialDeIntegracao>> ListarCredenciaisAsync(
        Guid integracaoId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Localiza a credencial pelo identificador publico, sem filtro de tenant.
    ///
    /// E a autenticacao: descobrir a organizacao E o objetivo, entao nao ha
    /// tenant pelo qual filtrar. A prova de posse e o segredo, conferido
    /// depois contra o hash.
    /// </summary>
    Task<CredencialDeIntegracao?> BuscarCredencialPorIdentificadorPublicoIgnorandoTenantAsync(
        string identificadorPublico,
        CancellationToken cancellationToken);

    /// <summary>Integracao e organizacao de uma credencial, sem filtro de tenant.</summary>
    Task<(Integracao Integracao, bool OrganizacaoAtiva)?> BuscarContextoDaCredencialIgnorandoTenantAsync(
        Guid integracaoId,
        CancellationToken cancellationToken);

    void Adicionar(Integracao integracao);

    void AdicionarCredencial(CredencialDeIntegracao credencial);
}

public interface IRepositorioDeTransacoes
{
    /// <summary>Transacao ja registrada com esta chave de idempotencia.</summary>
    Task<Transacao?> BuscarPorChaveDeIdempotenciaAsync(
        Guid integracaoId,
        string chaveDeIdempotencia,
        CancellationToken cancellationToken);

    /// <summary>Transacao ja registrada com este identificador de origem.</summary>
    Task<Transacao?> BuscarPorIdentificadorExternoAsync(
        Guid integracaoId,
        string identificadorExterno,
        CancellationToken cancellationToken);

    Task<Pagina<Transacao>> ListarAsync(
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken);

    Task<Transacao?> BuscarPorIdAsync(Guid transacaoId, CancellationToken cancellationToken);

    /// <summary>
    /// Varias transacoes do tenant atual, para o workspace do caso.
    ///
    /// Um caso pode reunir varios alertas, cada um sobre uma transacao. Buscar
    /// uma por vez seria N+1 na tela mais pesada do produto.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Transacao>> BuscarPorIdsAsync(
        IReadOnlyCollection<Guid> transacoesIds,
        CancellationToken cancellationToken);

    void Adicionar(Transacao transacao);
}
