using System.Security.Claims;
using CentralAntifraude.Application.Integracoes;
using CentralAntifraude.Domain.Integracoes;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Api.Integracoes;

/// <summary>
/// Le a integracao autenticada a partir das claims do esquema
/// <see cref="ManipuladorDeAutenticacaoDeIntegracao.Esquema"/>.
///
/// Mesmo desenho de <c>ContextoDoUsuarioAtual</c>: nada aqui olha para corpo,
/// query string ou cabecalho escolhido pelo cliente. Claim ausente ou
/// malformada resulta em <see cref="Guid.Empty"/>, que nao casa com nenhuma
/// linha — o filtro de tenant devolve vazio em vez de tudo.
/// </summary>
public sealed class ContextoDaIntegracaoAtual : IContextoDaIntegracaoAtual
{
    private readonly IHttpContextAccessor _acessor;

    public ContextoDaIntegracaoAtual(IHttpContextAccessor acessor) => _acessor = acessor;

    private ClaimsPrincipal? Principal => _acessor.HttpContext?.User;

    public bool EstaAutenticada =>
        Principal?.Identity?.IsAuthenticated == true &&
        OrganizacaoId != Guid.Empty &&
        IntegracaoId != Guid.Empty;

    public Guid OrganizacaoId => LerGuid(ManipuladorDeAutenticacaoDeIntegracao.ClaimDeOrganizacao);

    public Guid IntegracaoId => LerGuid(ManipuladorDeAutenticacaoDeIntegracao.ClaimDeIntegracao);

    private Guid LerGuid(string claim) =>
        Guid.TryParse(Principal?.FindFirstValue(claim), out var valor) ? valor : Guid.Empty;
}

// ---------------------------------------------------------------------------
// Entrada da ingestao.
//
// Contrato fechado. Com JsonUnmappedMemberHandling.Disallow ligado, um payload
// com "tenantId", "organizacaoId", "decisao" ou "score" e RECUSADO por conter
// campo desconhecido - o mass assignment vira 400, nao um campo ignorado em
// silencio.
// ---------------------------------------------------------------------------

/// <summary>Uma tentativa de pagamento, como a integracao a envia.</summary>
public sealed record RequisicaoDeIngestao(
    string? IdentificadorExterno,
    decimal Valor,
    string? Moeda,
    DateTimeOffset OcorridaEm,
    string? ClienteExternoId,
    string? ReferenciaDoInstrumento,
    string? FingerprintDoDispositivo,
    string? EnderecoIp,
    string? PaisDeOrigem);

public sealed record RequisicaoDeCriacaoDeIntegracao(string? Nome);

public sealed record RequisicaoDeRenomearIntegracao(string? Nome);

public sealed record RequisicaoDeAtivacaoDeIntegracao(bool Ativa);

// ---------------------------------------------------------------------------
// Saida.
// ---------------------------------------------------------------------------

/// <summary>
/// Confirmacao de recebimento.
///
/// Na Fase 2 a resposta e um recibo: a transacao foi registrada, com este
/// identificador. A decisao de risco entra na Fase 4, quando a avaliacao
/// passa a fazer parte da mesma operacao.
/// </summary>
public sealed record RespostaDeIngestao(
    Guid Id,
    string IdentificadorExterno,
    DateTimeOffset RecebidaEm,
    string Situacao)
{
    /// <summary>Registrada agora, nesta requisicao.</summary>
    public const string Registrada = "registrada";

    /// <summary>Ja existia. O pedido era um retry ou uma duplicata.</summary>
    public const string JaRegistrada = "ja_registrada";

    public static RespostaDeIngestao De(Transacao transacao, bool jaExistia)
    {
        ArgumentNullException.ThrowIfNull(transacao);

        return new RespostaDeIngestao(
            transacao.Id,
            transacao.IdentificadorExterno,
            transacao.RecebidaEm,
            jaExistia ? JaRegistrada : Registrada);
    }
}

/// <summary>Integracao na listagem administrativa.</summary>
public sealed record IntegracaoResumida(
    Guid Id,
    string Nome,
    bool Ativa,
    DateTimeOffset CriadaEm)
{
    public static IntegracaoResumida De(Integracao integracao)
    {
        ArgumentNullException.ThrowIfNull(integracao);

        return new IntegracaoResumida(
            integracao.Id,
            integracao.Nome,
            integracao.Ativa,
            integracao.CriadaEm);
    }
}

/// <summary>
/// Credencial na listagem.
///
/// Traz o identificador PUBLICO — que serve para reconhecer qual chave e qual
/// ao revogar — e nunca o segredo nem o hash dele.
/// </summary>
public sealed record CredencialResumida(
    Guid Id,
    string IdentificadorPublico,
    DateTimeOffset CriadaEm,
    DateTimeOffset? UsadaPelaUltimaVezEm,
    DateTimeOffset? RevogadaEm,
    string? MotivoDaRevogacao)
{
    public static CredencialResumida De(CredencialDeIntegracao credencial)
    {
        ArgumentNullException.ThrowIfNull(credencial);

        return new CredencialResumida(
            credencial.Id,
            credencial.IdentificadorPublico,
            credencial.CriadaEm,
            credencial.UsadaPelaUltimaVezEm,
            credencial.RevogadaEm,
            credencial.MotivoDaRevogacao?.ToString());
    }
}

/// <summary>
/// Resposta da emissao de credencial.
///
/// <see cref="Chave"/> aparece UMA unica vez, nesta resposta. Nao ha endpoint
/// que a mostre de novo, porque so o hash fica no banco. Se ela se perder, o
/// caminho e rotacionar.
/// </summary>
public sealed record CredencialEmitidaResposta(
    Guid Id,
    string IdentificadorPublico,
    string Chave,
    DateTimeOffset CriadaEm,
    string Aviso)
{
    public const string AvisoPadrao =
        "Guarde esta chave agora: ela nao sera exibida novamente. " +
        "Se perde-la, emita uma nova credencial.";

    public static CredencialEmitidaResposta De(CredencialEmitida emitida)
    {
        ArgumentNullException.ThrowIfNull(emitida);

        return new CredencialEmitidaResposta(
            emitida.Credencial.Id,
            emitida.Credencial.IdentificadorPublico,
            emitida.ValorBruto,
            emitida.Credencial.CriadaEm,
            AvisoPadrao);
    }
}

/// <summary>Integracao com suas credenciais.</summary>
public sealed record IntegracaoDetalhada(
    Guid Id,
    string Nome,
    bool Ativa,
    DateTimeOffset CriadaEm,
    IReadOnlyList<CredencialResumida> Credenciais);

/// <summary>Transacao na listagem operacional.</summary>
public sealed record TransacaoResumida(
    Guid Id,
    string IdentificadorExterno,
    decimal Valor,
    string Moeda,
    DateTimeOffset OcorridaEm,
    DateTimeOffset RecebidaEm,
    string ClienteExternoId,
    string? PaisDeOrigem)
{
    public static TransacaoResumida De(Transacao transacao)
    {
        ArgumentNullException.ThrowIfNull(transacao);

        return new TransacaoResumida(
            transacao.Id,
            transacao.IdentificadorExterno,
            transacao.Valor.Valor,
            transacao.Valor.Moeda,
            transacao.OcorridaEm,
            transacao.RecebidaEm,
            transacao.ClienteExternoId,
            transacao.PaisDeOrigem);
    }
}
