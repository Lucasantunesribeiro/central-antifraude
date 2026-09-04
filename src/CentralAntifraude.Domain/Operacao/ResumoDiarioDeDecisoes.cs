using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.Domain.Operacao;

/// <summary>
/// Quantas transacoes receberam cada decisao, por dia, em cada organizacao.
///
/// **Por que uma projecao, e por que ESTA.** O efeito assincrono desta fase
/// precisa ser simples e verificavel (ROADMAP 5.6) — alertas sao a Fase 6. Um
/// contador e a escolha certa por um motivo especifico: ele e o **pior caso**
/// para entrega duplicada. Somar duas vezes nao deixa rastro nenhum; o numero
/// simplesmente fica errado, e ninguem descobre.
///
/// Isso torna o teste de idempotencia honesto. Um efeito que fosse "criar
/// linha com chave unica" passaria mesmo com a Inbox desligada, porque a
/// restricao do banco seguraria a duplicata. O contador nao perdoa: ou a
/// Inbox funciona, ou o total mente.
///
/// A Fase 10 usa esta tabela no painel operacional.
/// </summary>
public sealed class ResumoDiarioDeDecisoes
{
    private ResumoDiarioDeDecisoes(
        Guid id,
        Guid organizacaoId,
        DateOnly dia,
        Decisao decisao,
        int quantidade,
        DateTimeOffset atualizadoEm)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        Dia = dia;
        Decisao = decisao;
        Quantidade = quantidade;
        AtualizadoEm = atualizadoEm;
    }

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    /// <summary>
    /// Dia da AVALIACAO, em UTC.
    ///
    /// Nao o dia da ocorrencia: uma transacao atrasada de tres dias atras foi
    /// decidida hoje, e e hoje que ela entrou na fila do analista. Agrupar
    /// pela ocorrencia faria o painel de hoje mudar retroativamente sempre que
    /// um evento atrasado chegasse.
    /// </summary>
    public DateOnly Dia { get; private set; }

    public Decisao Decisao { get; private set; }

    public int Quantidade { get; private set; }

    public DateTimeOffset AtualizadoEm { get; private set; }

    public static ResumoDiarioDeDecisoes Criar(
        Guid organizacaoId,
        DateOnly dia,
        Decisao decisao,
        DateTimeOffset agora)
    {
        if (organizacaoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Resumo diario exige organizacao.");
        }

        if (!Enum.IsDefined(decisao))
        {
            throw new ViolacaoDeInvariante($"Decisao desconhecida: {decisao}.");
        }

        return new ResumoDiarioDeDecisoes(Identificador.Novo(), organizacaoId, dia, decisao, 0, agora);
    }

    public void Somar(int quantidade, DateTimeOffset agora)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantidade);

        Quantidade += quantidade;
        AtualizadoEm = agora;
    }
}
