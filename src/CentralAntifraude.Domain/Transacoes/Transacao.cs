using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Tempo;

namespace CentralAntifraude.Domain.Transacoes;

/// <summary>
/// Uma tentativa de pagamento recebida pela Central Antifraude.
///
/// **A plataforma nao processa dinheiro** (CLAUDE.md secao 6). Isto e o
/// registro de uma tentativa que ja aconteceu — ou esta acontecendo — em
/// outro sistema, enviada para que o risco seja avaliado.
///
/// **Nada de dado financeiro completo** (secoes 56 e 58). Nao ha PAN, nao ha
/// CVV, nao ha conta bancaria. O instrumento de pagamento entra apenas como
/// referencia tokenizada do sistema de origem, e o proprio modelo torna isso
/// impossivel de violar: nao existe campo onde escrever um numero de cartao.
///
/// **Tres tempos distintos** (secao 15), nunca tratados como equivalentes:
/// <list type="bullet">
///   <item><see cref="OcorridaEm"/> — quando aconteceu na origem</item>
///   <item><see cref="RecebidaEm"/> — quando chegou aqui</item>
///   <item>avaliada em — chega na Fase 3, junto da avaliacao de risco</item>
/// </list>
/// A diferenca entre os dois primeiros e o que permite detectar evento
/// atrasado (secao 16) sem confundi-lo com evento recente.
/// </summary>
public sealed class Transacao
{
    public const int TamanhoMaximoDeIdentificadorExterno = 100;
    public const int TamanhoMaximoDeFingerprint = 128;

    private Transacao(
        Guid id,
        Guid organizacaoId,
        Guid integracaoId,
        string identificadorExterno,
        Dinheiro valor,
        DateTimeOffset ocorridaEm,
        DateTimeOffset recebidaEm,
        string clienteExternoId,
        string referenciaDoInstrumento,
        string? fingerprintDoDispositivo,
        string? fingerprintDoIp,
        string? paisDeOrigem,
        string chaveDeIdempotencia,
        string fingerprintDoPayload)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        IntegracaoId = integracaoId;
        IdentificadorExterno = identificadorExterno;
        Valor = valor;
        OcorridaEm = ocorridaEm;
        RecebidaEm = recebidaEm;
        ClienteExternoId = clienteExternoId;
        ReferenciaDoInstrumento = referenciaDoInstrumento;
        FingerprintDoDispositivo = fingerprintDoDispositivo;
        FingerprintDoIp = fingerprintDoIp;
        PaisDeOrigem = paisDeOrigem;
        ChaveDeIdempotencia = chaveDeIdempotencia;
        FingerprintDoPayload = fingerprintDoPayload;
    }

    // Construtor usado pelo EF Core na materializacao.
    private Transacao()
    {
        IdentificadorExterno = string.Empty;
        ClienteExternoId = string.Empty;
        ReferenciaDoInstrumento = string.Empty;
        ChaveDeIdempotencia = string.Empty;
        FingerprintDoPayload = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Tenant. Vem da credencial da integracao, jamais do payload.</summary>
    public Guid OrganizacaoId { get; private set; }

    public Guid IntegracaoId { get; private set; }

    /// <summary>
    /// Identificador da transacao no sistema de origem. Unico por
    /// organizacao + integracao — e a segunda barreira de idempotencia.
    /// </summary>
    public string IdentificadorExterno { get; private set; }

    public Dinheiro Valor { get; private set; }

    /// <summary>Quando a tentativa aconteceu no sistema de origem.</summary>
    public DateTimeOffset OcorridaEm { get; private set; }

    /// <summary>Quando chegou a Central Antifraude.</summary>
    public DateTimeOffset RecebidaEm { get; private set; }

    /// <summary>
    /// Quanto tempo o evento levou para chegar.
    ///
    /// Regras de velocidade da Fase 3 dependem de janela temporal; uma
    /// transacao que chega com horas de atraso nao pode ser tratada como se
    /// tivesse acabado de acontecer.
    /// </summary>
    public TimeSpan AtrasoAteRecebimento => RecebidaEm - OcorridaEm;

    /// <summary>Identificador do cliente no sistema de origem. Nao e CPF nem nome.</summary>
    public string ClienteExternoId { get; private set; }

    /// <summary>
    /// Token do instrumento de pagamento, fornecido pela origem.
    /// Ex.: <c>pi_demo_123</c>. Nunca um numero de cartao.
    /// </summary>
    public string ReferenciaDoInstrumento { get; private set; }

    /// <summary>Identificador de dispositivo fornecido pela origem.</summary>
    public string? FingerprintDoDispositivo { get; private set; }

    /// <summary>
    /// HMAC do endereco IP — nunca o IP em si (CLAUDE.md secao 57).
    ///
    /// Permite responder "estas duas transacoes vieram do mesmo lugar?", que e
    /// o que uma regra de risco precisa, sem guardar um dado que identifica
    /// pessoa e que nao ha motivo para reter.
    /// </summary>
    public string? FingerprintDoIp { get; private set; }

    /// <summary>Pais de origem em ISO 3166-1 alfa-2, derivado pela origem.</summary>
    public string? PaisDeOrigem { get; private set; }

    /// <summary>Chave de idempotencia enviada pela integracao.</summary>
    public string ChaveDeIdempotencia { get; private set; }

    /// <summary>
    /// Hash canonico do conteudo da requisicao.
    ///
    /// E o que distingue "mesma chave, mesmo pedido" — que deve devolver o
    /// resultado original — de "mesma chave, pedido diferente", que e
    /// conflito. Ver <c>FingerprintDaRequisicao</c>.
    /// </summary>
    public string FingerprintDoPayload { get; private set; }

    public static Transacao Registrar(
        Guid organizacaoId,
        Guid integracaoId,
        string identificadorExterno,
        Dinheiro valor,
        DateTimeOffset ocorridaEm,
        DateTimeOffset recebidaEm,
        string clienteExternoId,
        string referenciaDoInstrumento,
        string? fingerprintDoDispositivo,
        string? fingerprintDoIp,
        string? paisDeOrigem,
        string chaveDeIdempotencia,
        string fingerprintDoPayload)
    {
        if (organizacaoId == Guid.Empty || integracaoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Transacao exige organizacao e integracao.");
        }

        if (!valor.EhValido)
        {
            throw new ViolacaoDeInvariante("Transacao exige um valor monetario valido.");
        }

        if (valor.Valor <= 0)
        {
            throw new ViolacaoDeInvariante("Valor de uma tentativa de pagamento deve ser positivo.");
        }

        ExigirTexto(identificadorExterno, nameof(identificadorExterno), TamanhoMaximoDeIdentificadorExterno);
        ExigirTexto(clienteExternoId, nameof(clienteExternoId), TamanhoMaximoDeIdentificadorExterno);
        ExigirTexto(referenciaDoInstrumento, nameof(referenciaDoInstrumento), TamanhoMaximoDeIdentificadorExterno);
        ExigirTexto(chaveDeIdempotencia, nameof(chaveDeIdempotencia), TamanhoMaximoDeIdentificadorExterno);
        ExigirTexto(fingerprintDoPayload, nameof(fingerprintDoPayload), TamanhoMaximoDeFingerprint);

        return new Transacao(
            Identificador.Novo(),
            organizacaoId,
            integracaoId,
            identificadorExterno.Trim(),
            valor,
            // Normalizado, e nao apenas convertido para UTC: o horario que
            // a integracao envia pode ter precisao maior do que o banco
            // guarda, e sem truncar aqui a transacao em memoria e a lida
            // de volta teriam OcorridaEm diferentes.
            Instante.Normalizar(ocorridaEm),
            Instante.Normalizar(recebidaEm),
            clienteExternoId.Trim(),
            referenciaDoInstrumento.Trim(),
            fingerprintDoDispositivo?.Trim(),
            fingerprintDoIp,
            paisDeOrigem?.Trim().ToUpperInvariant(),
            chaveDeIdempotencia.Trim(),
            fingerprintDoPayload);
    }

    private static void ExigirTexto(string valor, string campo, int tamanhoMaximo)
    {
        if (string.IsNullOrWhiteSpace(valor) || valor.Trim().Length > tamanhoMaximo)
        {
            throw new ViolacaoDeInvariante(
                $"{campo} e obrigatorio e deve ter no maximo {tamanhoMaximo} caracteres.");
        }
    }
}
