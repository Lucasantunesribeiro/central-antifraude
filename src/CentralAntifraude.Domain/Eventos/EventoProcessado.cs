using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Eventos;

/// <summary>
/// A Inbox: o registro de que um consumidor ja tratou um evento.
///
/// **Por que ela precisa existir** (CLAUDE.md secao 42). A entrega e
/// at-least-once por desenho: o mesmo evento chega duas vezes sempre que o
/// processo cai entre o efeito e a confirmacao, ou quando a visibilidade
/// expira antes de o trabalho terminar. Sem a Inbox, cada reentrega repete o
/// efeito — e um contador que soma duas vezes nao tem como saber que errou.
///
/// **Memoria local nao serve.** Um <c>HashSet</c> no worker esquece tudo a
/// cada reinicio, e nao vale nada quando ha mais de um worker. O registro
/// precisa estar no mesmo banco do efeito, e ser gravado na MESMA transacao —
/// senao existe o instante em que o efeito aconteceu e a marca nao, que e
/// exatamente o buraco que a Inbox deveria fechar.
///
/// **A chave inclui o consumidor.** Dois consumidores diferentes precisam
/// tratar o mesmo evento, cada um uma vez. Marcar so pelo evento faria o
/// segundo consumidor achar que o trabalho dele ja tinha sido feito.
/// </summary>
public sealed class EventoProcessado
{
    public const int TamanhoMaximoDoConsumidor = 80;

    private EventoProcessado(
        Guid id,
        Guid organizacaoId,
        Guid eventoId,
        string consumidor,
        string tipoDoEvento,
        string idDeCorrelacao,
        DateTimeOffset processadoEm)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        EventoId = eventoId;
        Consumidor = consumidor;
        TipoDoEvento = tipoDoEvento;
        IdDeCorrelacao = idDeCorrelacao;
        ProcessadoEm = processadoEm;
    }

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    /// <summary>O <c>eventId</c> do envelope — o mesmo em toda reentrega.</summary>
    public Guid EventoId { get; private set; }

    /// <summary>Quem tratou. Faz parte da chave de unicidade.</summary>
    public string Consumidor { get; private set; } = string.Empty;

    public string TipoDoEvento { get; private set; } = string.Empty;

    /// <summary>
    /// Correlacao da requisicao HTTP que originou tudo isto.
    ///
    /// Gravada no efeito, e nao apenas no log: o log some com a retencao, e a
    /// pergunta "qual requisicao causou este numero no painel?" continua
    /// valendo depois disso. E o ultimo elo da corrente que o CLAUDE.md secao
    /// 69 exige — HTTP, dominio, outbox, mensagem, worker, efeito.
    /// </summary>
    public string IdDeCorrelacao { get; private set; } = string.Empty;

    public DateTimeOffset ProcessadoEm { get; private set; }

    public static EventoProcessado Registrar(
        Guid organizacaoId,
        Guid eventoId,
        string consumidor,
        string tipoDoEvento,
        string idDeCorrelacao,
        DateTimeOffset agora)
    {
        if (organizacaoId == Guid.Empty || eventoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Evento processado exige organizacao e evento.");
        }

        if (string.IsNullOrWhiteSpace(consumidor) ||
            consumidor.Trim().Length > TamanhoMaximoDoConsumidor)
        {
            throw new ViolacaoDeInvariante(
                $"Consumidor e obrigatorio e deve ter no maximo {TamanhoMaximoDoConsumidor} caracteres.");
        }

        return new EventoProcessado(
            Identificador.Novo(),
            organizacaoId,
            eventoId,
            consumidor.Trim(),
            tipoDoEvento,
            idDeCorrelacao ?? string.Empty,
            agora);
    }
}
