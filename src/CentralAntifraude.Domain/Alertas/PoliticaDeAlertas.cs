using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.Domain.Alertas;

/// <summary>
/// Quando uma avaliacao vira alerta, e com que prioridade.
///
/// **Isto nao e um segundo motor de risco** (ROADMAP 6.2). Ela nao le
/// historico, nao consulta banco, nao soma pontos e nao reavalia nada: recebe
/// a decisao que o motor ja tomou e responde uma pergunta operacional — este
/// resultado precisa de olho humano, e com que urgencia?
///
/// Reavaliar aqui seria pior do que redundante. O consumidor roda depois, em
/// outro processo, e com o catalogo de regras possivelmente ja republicado —
/// ele chegaria a um resultado diferente do que o integrador recebeu na
/// resposta sincrona, e o painel passaria a discordar do recibo.
///
/// **A politica e codigo, e nao configuracao de banco.** O `CLAUDE.md` secao
/// 21 recusa DSL, SQL configuravel e script vindo do banco; a mesma razao vale
/// aqui. O que muda com o tempo e a versao — e ela fica gravada em cada
/// alerta, para que "por que este alerta e Alta?" continue tendo resposta
/// depois que a politica mudar.
///
/// **Os valores sao configuracao de demonstracao, nao pratica de mercado.**
/// O `CLAUDE.md` secao 112 proibe apresentar numero inventado como padrao do
/// setor. O que existe de fundamentado e a tripla permitir/revisar/bloquear,
/// documentada na ADR 0008; o mapa abaixo e a escolha deste produto.
/// </summary>
public static class PoliticaDeAlertas
{
    /// <summary>
    /// Versao desta politica.
    ///
    /// Gravada em cada alerta. Sobe quando o mapa decisao -> prioridade muda,
    /// e nunca reescreve alertas antigos: um alerta criado sob a v1 continua
    /// dizendo que foi a v1 que o classificou.
    /// </summary>
    public const int Versao = 1;

    /// <summary>
    /// Prioridade do alerta, ou <c>null</c> quando a decisao nao gera alerta.
    ///
    /// `Permitir` nao gera alerta de proposito. Uma fila que recebesse toda
    /// transacao permitida deixaria de ser uma fila de trabalho e viraria um
    /// espelho da tabela de transacoes — que ja existe, na tela de Transacoes.
    /// </summary>
    public static PrioridadeDeAlerta? Classificar(Decisao decisao) => decisao switch
    {
        Decisao.Permitir => null,
        Decisao.Revisar => PrioridadeDeAlerta.Media,
        Decisao.Bloquear => PrioridadeDeAlerta.Alta,
        _ => throw new ViolacaoDeInvariante(
            $"Decisao desconhecida na politica de alertas: {decisao}. " +
            "Uma decisao nova precisa de uma linha aqui — silenciar seria deixar de alertar."),
    };

    /// <summary>
    /// Monta o alerta que este evento merece, ou <c>null</c> quando ele nao
    /// merece nenhum.
    ///
    /// Funcao pura: mesmos argumentos, mesmo resultado. E o que permite testar
    /// a politica inteira sem banco, sem fila e sem relogio real.
    /// </summary>
    public static Alerta? Avaliar(
        Guid organizacaoId,
        TransacaoAvaliadaV1 conteudo,
        Guid eventoId,
        string idDeCorrelacao,
        DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(conteudo);

        var prioridade = Classificar(conteudo.Decisao);

        if (prioridade is null)
        {
            return null;
        }

        return Alerta.Registrar(
            organizacaoId,
            conteudo.AvaliacaoId,
            conteudo.TransacaoId,
            conteudo.Decisao,
            conteudo.Score,
            prioridade.Value,
            Versao,
            eventoId,
            idDeCorrelacao,
            conteudo.AvaliadaEm,
            agora);
    }
}
