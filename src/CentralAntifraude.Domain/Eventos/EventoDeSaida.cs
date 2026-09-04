using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Eventos;

/// <summary>
/// Uma linha da Outbox: um evento que precisa sair, gravado junto da mudanca
/// que o originou.
///
/// **O problema que isto resolve** (CLAUDE.md secao 38). Gravar a avaliacao no
/// banco e publicar a mensagem no broker sao duas escritas em sistemas
/// diferentes. Sem coordenacao, o processo pode cair entre uma e outra:
///
/// - publica e nao grava → o consumidor age sobre uma decisao que nao existe;
/// - grava e nao publica → a decisao existe e nenhum efeito acontece.
///
/// A Outbox elimina o primeiro caso e transforma o segundo em atraso. A linha
/// de evento entra na **mesma transacao** da avaliacao: ou as duas existem, ou
/// nenhuma. Um despachante separado le as pendentes e publica depois.
///
/// **O que ela NAO promete.** Se a mensagem for enviada ao broker e o processo
/// cair antes de marcar <see cref="PublicadoEm"/>, ela sera enviada de novo.
/// Isso e esperado (CLAUDE.md secao 40): a entrega e at-least-once, e a
/// correcao pertence ao consumidor idempotente — nao a uma promessa de
/// exatamente-uma-vez que ninguem consegue cumprir.
///
/// Nesta fase a linha e apenas **gravada**. Despacho, SQS, Inbox e DLQ sao a
/// Fase 5; <see cref="PublicadoEm"/> e <see cref="TentativasDePublicacao"/>
/// existem desde agora para que o esquema nao precise mudar la.
/// </summary>
public sealed class EventoDeSaida
{
    public const int TamanhoMaximoDoTipo = 80;

    public const int TamanhoMaximoDaCorrelacao = 64;

    private EventoDeSaida(
        Guid id,
        Guid organizacaoId,
        string tipo,
        DateTimeOffset ocorridoEm,
        string idDeCorrelacao,
        ConteudoDeEvento conteudo)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        Tipo = tipo;
        OcorridoEm = ocorridoEm;
        IdDeCorrelacao = idDeCorrelacao;
        Conteudo = conteudo;
    }

    /// <summary>
    /// Identificador do evento — o <c>eventId</c> do envelope.
    ///
    /// E ele que o consumidor grava na Inbox para reconhecer a mesma mensagem
    /// entregue duas vezes (Fase 5). Por isso e sorteado uma unica vez, aqui,
    /// e nunca regenerado no despacho: um identificador novo a cada tentativa
    /// tornaria a Inbox inutil.
    /// </summary>
    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    /// <summary>Nome versionado do tipo, ex. <c>TransacaoAvaliada.v1</c>.</summary>
    public string Tipo { get; private set; } = string.Empty;

    /// <summary>Quando o fato aconteceu no dominio — nao quando foi publicado.</summary>
    public DateTimeOffset OcorridoEm { get; private set; }

    /// <summary>
    /// Correlacao da operacao que produziu o evento.
    ///
    /// E o fio que liga a requisicao HTTP do integrador ao efeito que um
    /// worker vai executar minutos depois (CLAUDE.md secao 69). Sem ele, uma
    /// investigacao ponta a ponta vira arqueologia por horario.
    /// </summary>
    public string IdDeCorrelacao { get; private set; } = string.Empty;

    /// <summary>Conteudo tipado do evento, gravado como <c>jsonb</c>.</summary>
    public ConteudoDeEvento Conteudo { get; private set; } = null!;

    /// <summary>Nulo enquanto pendente. Preenchido pelo despachante da Fase 5.</summary>
    public DateTimeOffset? PublicadoEm { get; private set; }

    /// <summary>Quantas vezes o despacho ja foi tentado.</summary>
    public int TentativasDePublicacao { get; private set; }

    public static EventoDeSaida Registrar(
        Guid organizacaoId,
        ConteudoDeEvento conteudo,
        DateTimeOffset ocorridoEm,
        string idDeCorrelacao)
    {
        ArgumentNullException.ThrowIfNull(conteudo);

        if (organizacaoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Evento de saida exige organizacao.");
        }

        if (string.IsNullOrWhiteSpace(idDeCorrelacao) ||
            idDeCorrelacao.Trim().Length > TamanhoMaximoDaCorrelacao)
        {
            throw new ViolacaoDeInvariante(
                $"IdDeCorrelacao e obrigatorio e deve ter no maximo {TamanhoMaximoDaCorrelacao} caracteres.");
        }

        if (conteudo.Tipo.Length > TamanhoMaximoDoTipo)
        {
            throw new ViolacaoDeInvariante(
                $"O tipo do evento deve ter no maximo {TamanhoMaximoDoTipo} caracteres.");
        }

        return new EventoDeSaida(
            Identificador.Novo(),
            organizacaoId,
            conteudo.Tipo,
            ocorridoEm,
            idDeCorrelacao.Trim(),
            conteudo);
    }

    /// <summary>
    /// Marca o evento como publicado.
    ///
    /// Chamado pelo despachante da Fase 5, sempre DEPOIS do envio bem
    /// sucedido. Marcar antes transformaria uma falha de rede em evento
    /// perdido para sempre.
    /// </summary>
    public void MarcarPublicado(DateTimeOffset agora)
    {
        PublicadoEm = agora;
        TentativasDePublicacao++;
    }

    /// <summary>Registra uma tentativa que falhou, sem marcar como publicado.</summary>
    public void RegistrarTentativaFalha() => TentativasDePublicacao++;
}
