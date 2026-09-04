namespace CentralAntifraude.Domain.Eventos;

/// <summary>
/// O que viaja na fila.
///
/// **Metadados fora do conteudo, de proposito** (CLAUDE.md secao 41). Um
/// consumidor precisa decidir se sabe ler a mensagem ANTES de tentar
/// interpreta-la. Se o tipo e a versao estivessem enterrados no payload, ele
/// teria que desserializar para descobrir que nao deveria ter desserializado.
///
/// **Tipo e versao sao campos separados.** `TransacaoAvaliada` + `1` permite a
/// pergunta "eu sei lidar com este evento em alguma versao?" sem parsing de
/// string. Um consumidor que entende a v1 e recebe a v2 recusa de forma
/// explicita, em vez de tentar ler campos que mudaram de significado.
///
/// **Nao ha payload polimorfico sem contrato** (ROADMAP 5.3): o conteudo e um
/// <see cref="ConteudoDeEvento"/> tipado, e a lista de tipos conhecidos e
/// fechada.
/// </summary>
public sealed record EnvelopeDeEvento
{
    /// <summary>Separador entre nome e versao no tipo composto: <c>Nome.vN</c>.</summary>
    public const string SeparadorDeVersao = ".v";

    private EnvelopeDeEvento(
        Guid eventId,
        string eventType,
        int version,
        Guid tenantId,
        string correlationId,
        DateTimeOffset occurredAt,
        ConteudoDeEvento conteudo)
    {
        EventId = eventId;
        EventType = eventType;
        Version = version;
        TenantId = tenantId;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
        Conteudo = conteudo;
    }

    /// <summary>
    /// Identificador do evento. E a chave da Inbox do consumidor.
    ///
    /// O mesmo evento entregue duas vezes chega com o MESMO EventId — e por
    /// isso o consumidor consegue reconhecer a repeticao. Se cada entrega
    /// trouxesse um identificador novo, a Inbox nao serviria para nada.
    /// </summary>
    public Guid EventId { get; }

    /// <summary>Nome do tipo, sem versao. Ex.: <c>TransacaoAvaliada</c>.</summary>
    public string EventType { get; }

    public int Version { get; }

    /// <summary>
    /// Organizacao dona do evento.
    ///
    /// O consumidor NAO confia neste campo sozinho: ele confere contra o dado
    /// que carrega no proprio banco. Um envelope que afirme um tenant e
    /// aponte para uma transacao de outro e mensagem invalida, nao instrucao.
    /// </summary>
    public Guid TenantId { get; }

    /// <summary>Correlacao da requisicao HTTP que originou tudo isto.</summary>
    public string CorrelationId { get; }

    /// <summary>Quando o fato aconteceu no dominio — nunca quando foi publicado.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>Conteudo tipado, correspondente a <see cref="EventType"/> e <see cref="Version"/>.</summary>
    public ConteudoDeEvento Conteudo { get; }

    /// <summary>Tipo composto, como fica gravado na Outbox: <c>Nome.vN</c>.</summary>
    public string TipoComposto => Compor(EventType, Version);

    public static EnvelopeDeEvento De(EventoDeSaida evento)
    {
        ArgumentNullException.ThrowIfNull(evento);

        var (nome, versao) = Decompor(evento.Tipo);

        return new EnvelopeDeEvento(
            evento.Id,
            nome,
            versao,
            evento.OrganizacaoId,
            evento.IdDeCorrelacao,
            evento.OcorridoEm,
            evento.Conteudo);
    }

    /// <summary>Reconstroi um envelope recebido da fila.</summary>
    public static EnvelopeDeEvento Reconstruir(
        Guid eventId,
        string eventType,
        int version,
        Guid tenantId,
        string correlationId,
        DateTimeOffset occurredAt,
        ConteudoDeEvento conteudo)
    {
        ArgumentNullException.ThrowIfNull(conteudo);

        if (eventId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Envelope sem eventId nao pode ser processado.");
        }

        if (tenantId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Envelope sem tenantId nao pode ser processado.");
        }

        if (string.IsNullOrWhiteSpace(eventType) || version < 1)
        {
            throw new ViolacaoDeInvariante("Envelope sem tipo ou com versao invalida.");
        }

        // O conteudo tem que corresponder ao que o envelope declara. Um
        // envelope que diz "TransacaoAvaliada v1" e carrega outra coisa e
        // mensagem forjada ou bug de despacho - nos dois casos, recusar e a
        // resposta certa.
        if (!string.Equals(conteudo.Tipo, Compor(eventType, version), StringComparison.Ordinal))
        {
            throw new ViolacaoDeInvariante(
                $"O envelope declara '{Compor(eventType, version)}' mas carrega '{conteudo.Tipo}'.");
        }

        return new EnvelopeDeEvento(
            eventId,
            eventType,
            version,
            tenantId,
            correlationId ?? string.Empty,
            occurredAt,
            conteudo);
    }

    public static string Compor(string nome, int versao) =>
        $"{nome}{SeparadorDeVersao}{versao.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Separa <c>Nome.vN</c> em nome e versao.
    ///
    /// Formato invalido falha alto. Um tipo que ninguem consegue decompor nao
    /// pode virar um evento "sem versao" que o consumidor aceita por engano.
    /// </summary>
    public static (string Nome, int Versao) Decompor(string tipoComposto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tipoComposto);

        var corte = tipoComposto.LastIndexOf(SeparadorDeVersao, StringComparison.Ordinal);

        if (corte <= 0 ||
            !int.TryParse(
                tipoComposto[(corte + SeparadorDeVersao.Length)..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var versao) ||
            versao < 1)
        {
            throw new ViolacaoDeInvariante(
                $"Tipo de evento '{tipoComposto}' nao segue o formato Nome.vN.");
        }

        return (tipoComposto[..corte], versao);
    }
}
