using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.Application.Operacao;

/// <summary>
/// A janela que o painel resume.
///
/// Um numero de dias, e nao duas datas livres: o painel responde "como esta a
/// operacao", e essa pergunta tem um horizonte curto. Intervalos arbitrarios
/// pertencem ao console de transacoes, que ja os aceita.
/// </summary>
public sealed record JanelaDoPainel
{
    public const int DiasPadrao = 7;
    public const int DiasMaximos = 90;

    private JanelaDoPainel(int dias, DateTimeOffset de, DateTimeOffset ate)
    {
        Dias = dias;
        De = de;
        Ate = ate;
    }

    public int Dias { get; }

    public DateTimeOffset De { get; }

    public DateTimeOffset Ate { get; }

    public static bool TentarCriar(
        int? dias,
        DateTimeOffset agora,
        out JanelaDoPainel janela,
        out string erro)
    {
        var efetivos = dias ?? DiasPadrao;

        janela = new JanelaDoPainel(DiasPadrao, agora.AddDays(-DiasPadrao), agora);

        if (efetivos is < 1 or > DiasMaximos)
        {
            erro = $"O periodo do painel deve estar entre 1 e {DiasMaximos} dias.";
            return false;
        }

        janela = new JanelaDoPainel(efetivos, agora.AddDays(-efetivos), agora);
        erro = string.Empty;

        return true;
    }
}

/// <summary>Quantas avaliacoes cairam em cada decisao.</summary>
public sealed record ContagemPorDecisao(Decisao Decisao, int Quantidade);

/// <summary>Um dia da serie temporal do painel.</summary>
public sealed record DiaDoPainel(DateOnly Dia, int Permitir, int Revisar, int Bloquear)
{
    public int Total => Permitir + Revisar + Bloquear;
}

/// <summary>Um tipo de sinal e quantas vezes ele apareceu no periodo.</summary>
public sealed record SinalFrequente(TipoDeRegra Tipo, int Acionamentos);

/// <summary>Quantos casos existem em cada situacao.</summary>
public sealed record ContagemPorStatusDeCaso(StatusDoCaso Status, int Quantidade);

/// <summary>
/// O que o painel operacional responde.
///
/// **Nenhum numero aqui e decorativo** (ROADMAP 10.4). Cada campo existe
/// porque alguem precisa dele para decidir o que fazer nas proximas horas: o
/// que chegou, o que o motor recomendou, o que espera gente e o que esta
/// esperando ha tempo demais.
/// </summary>
public sealed record ResumoOperacional(
    JanelaDoPainel Janela,
    int TransacoesRecebidas,
    int TransacoesAvaliadas,
    IReadOnlyList<ContagemPorDecisao> Decisoes,
    IReadOnlyList<DiaDoPainel> Tendencia,
    IReadOnlyList<SinalFrequente> SinaisMaisFrequentes,
    int AlertasAbertos,
    IReadOnlyList<ContagemPorStatusDeCaso> Casos,
    int CasosAntigos,
    int EventosPendentes);

/// <summary>
/// Quantas vezes uma regra acionou, e o que a investigacao humana concluiu
/// sobre as transacoes que ela marcou.
///
/// **Isto e contagem, e nao avaliacao de qualidade da regra** (ROADMAP 10.5).
/// As transacoes com veredito nao sao amostra aleatoria — elas viraram caso
/// justamente porque o motor as marcou. Uma regra com muitas fraudes
/// confirmadas pode estar apontando bem, ou pode ser a unica que a equipe
/// costuma investigar. Correlacao nao e causalidade, e o produto nao afirma
/// o contrario em lugar nenhum.
/// </summary>
public sealed record MetricaDeRegra(
    Guid RegraId,
    string Nome,
    TipoDeRegra Tipo,
    int Acionamentos,
    int FraudeConfirmada,
    int Legitima,
    int Inconclusiva,
    int SemResultadoConhecido);

/// <summary>Consultas agregadas do painel e das metricas de regra.</summary>
public interface IRepositorioDeOperacao
{
    /// <summary>Transacoes cujo <c>RecebidaEm</c> cai na janela.</summary>
    Task<int> ContarTransacoesRecebidasAsync(JanelaDoPainel janela, CancellationToken cancellationToken);

    /// <summary>
    /// Avaliacoes por decisao, ancoradas em <c>AvaliadaEm</c>.
    ///
    /// A ancora e a avaliacao, e nao a ocorrencia: uma transacao atrasada de
    /// tres dias atras foi **decidida hoje**, e e hoje que ela entrou na fila
    /// do analista. Agrupar pela ocorrencia faria o painel de hoje mudar
    /// retroativamente sempre que um evento atrasado chegasse — a mesma razao
    /// registrada em <see cref="Domain.Operacao.ResumoDiarioDeDecisoes"/>.
    /// </summary>
    Task<IReadOnlyList<ContagemPorDecisao>> ContarDecisoesAsync(
        JanelaDoPainel janela,
        CancellationToken cancellationToken);

    /// <summary>Serie diaria de decisoes, sem buracos entre os dias.</summary>
    Task<IReadOnlyList<DiaDoPainel>> ContarDecisoesPorDiaAsync(
        JanelaDoPainel janela,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SinalFrequente>> ContarSinaisAsync(
        JanelaDoPainel janela,
        int limite,
        CancellationToken cancellationToken);

    Task<int> ContarAlertasAbertosAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ContagemPorStatusDeCaso>> ContarCasosAsync(CancellationToken cancellationToken);

    /// <summary>Casos ainda nao resolvidos, abertos antes de <paramref name="limite"/>.</summary>
    Task<int> ContarCasosAntigosAsync(DateTimeOffset limite, CancellationToken cancellationToken);

    /// <summary>
    /// Eventos ainda nao publicados na Outbox do tenant.
    ///
    /// E a medida direta de "o caminho assincrono esta andando". Um numero que
    /// nao volta a zero significa alerta que nao vai ser criado — e o analista
    /// descobriria isso pelo silencio da fila.
    /// </summary>
    Task<int> ContarEventosPendentesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MetricaDeRegra>> ApurarMetricasDeRegraAsync(
        JanelaDoPainel janela,
        CancellationToken cancellationToken);

    /// <summary>
    /// Alertas, caso e veredito de uma transacao, em uma consulta.
    ///
    /// Fica junto das agregacoes do painel, e nao no repositorio de alertas,
    /// porque atravessa tres agregados — alerta, caso e resultado de
    /// investigacao. Espalhar isso em tres chamadas faria a tela de detalhe
    /// pagar tres idas ao banco para responder uma pergunta so.
    /// </summary>
    Task<ContextoOperacionalDaTransacao> CarregarContextoDaTransacaoAsync(
        Guid transacaoId,
        CancellationToken cancellationToken);
}
