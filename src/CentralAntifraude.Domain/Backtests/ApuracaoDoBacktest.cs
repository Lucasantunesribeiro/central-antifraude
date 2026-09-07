using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.Domain.Backtests;

/// <summary>
/// O que uma transacao produziu nos dois perfis.
///
/// Os dois lados sao calculados na MESMA passagem, sobre o mesmo contexto
/// historico. Comparar o candidato com a avaliacao que ficou gravada seria
/// mais barato e enganoso: dentro de uma janela de noventa dias o perfil pode
/// ter sido publicado tres vezes, e "quantas decisoes mudariam" passaria a
/// misturar o efeito do candidato com o efeito de mudancas ja feitas.
/// </summary>
public sealed record ObservacaoDoBacktest(
    int ScoreVigente,
    Decisao DecisaoVigente,
    int ScoreCandidato,
    Decisao DecisaoCandidata,
    bool CandidatoAcionou,
    ResultadoDaInvestigacao? Veredito);

/// <summary>
/// Acumula as observacoes e fecha o resultado.
///
/// **Puro de proposito**, como o motor: sem banco, sem relogio, sem ordem
/// dependente de I/O. Duas execucoes sobre as mesmas observacoes produzem o
/// mesmo resultado, byte a byte — o que permite testar a apuracao sem
/// levantar nada.
/// </summary>
public sealed class ApuracaoDoBacktest
{
    /// <summary>
    /// Bordas das faixas de score, em ordem.
    ///
    /// Cinco faixas de vinte pontos. Nao coincidem com os limiares de decisao
    /// de proposito: os limiares mudam entre o perfil vigente e o candidato, e
    /// faixas que mudassem junto tornariam as duas colunas incomparaveis.
    /// </summary>
    private static readonly int[] Bordas = [0, 20, 40, 60, 80, 101];

    /// <summary>
    /// As quatro linhas do cruzamento por veredito, em ordem fixa.
    ///
    /// Um vetor, e nao um dicionario: a chave seria um enum ANULAVEL — "sem
    /// resultado conhecido" e uma das quatro linhas — e um dicionario nao
    /// aceita chave anulavel. O vetor tambem garante a ordem sem precisar
    /// ordena-la depois.
    /// </summary>
    private static readonly ResultadoDaInvestigacao?[] Vereditos =
    [
        ResultadoDaInvestigacao.FraudeConfirmada,
        ResultadoDaInvestigacao.Legitima,
        ResultadoDaInvestigacao.Inconclusiva,
        null,
    ];

    private readonly Dictionary<(Decisao De, Decisao Para), int> _mudancas = [];
    private readonly Acumulador[] _porVeredito =
        [.. Enumerable.Range(0, Vereditos.Length).Select(_ => new Acumulador())];
    private readonly int[] _faixasVigente = new int[Bordas.Length - 1];
    private readonly int[] _faixasCandidato = new int[Bordas.Length - 1];

    private int _total;
    private int _acionaria;
    private DistribuicaoDeDecisoes _vigente = DistribuicaoDeDecisoes.Vazia;
    private DistribuicaoDeDecisoes _candidato = DistribuicaoDeDecisoes.Vazia;

    public void Registrar(ObservacaoDoBacktest observacao)
    {
        ArgumentNullException.ThrowIfNull(observacao);

        _total++;

        if (observacao.CandidatoAcionou)
        {
            _acionaria++;
        }

        _vigente = _vigente.Somar(observacao.DecisaoVigente);
        _candidato = _candidato.Somar(observacao.DecisaoCandidata);

        _faixasVigente[FaixaDe(observacao.ScoreVigente)]++;
        _faixasCandidato[FaixaDe(observacao.ScoreCandidato)]++;

        if (observacao.DecisaoVigente != observacao.DecisaoCandidata)
        {
            var chave = (observacao.DecisaoVigente, observacao.DecisaoCandidata);
            _mudancas[chave] = _mudancas.TryGetValue(chave, out var atual) ? atual + 1 : 1;
        }

        _porVeredito[PosicaoDe(observacao.Veredito)].Registrar(observacao);
    }

    /// <summary>
    /// Fecha o resultado.
    ///
    /// Toda lista sai em ordem declarada — nunca na ordem em que o dicionario
    /// devolve. Dois backtests com os mesmos numeros precisam produzir o mesmo
    /// documento, senao comparar duas execucoes vira leitura de diff falso.
    /// </summary>
    public ResultadoDoBacktest Concluir() =>
        new(
            _total,
            _acionaria,
            _vigente,
            _candidato,
            [
                .. _mudancas
                    .OrderBy(par => par.Key.De)
                    .ThenBy(par => par.Key.Para)
                    .Select(par => new MudancaDeDecisao(par.Key.De, par.Key.Para, par.Value)),
            ],
            [
                .. Enumerable.Range(0, Vereditos.Length)
                    // Linha vazia nao aparece: dizer "0 fraudes confirmadas,
                    // das quais 0 seriam bloqueadas" e ruido, nao informacao.
                    .Where(posicao => _porVeredito[posicao].Total > 0)
                    .Select(posicao => new LinhaPorVeredito(
                        Vereditos[posicao],
                        _porVeredito[posicao].Total,
                        _porVeredito[posicao].Vigente,
                        _porVeredito[posicao].Candidato)),
            ],
            [
                .. Enumerable.Range(0, Bordas.Length - 1).Select(indice => new FaixaDeScore(
                    Bordas[indice],
                    Bordas[indice + 1] - 1,
                    _faixasVigente[indice],
                    _faixasCandidato[indice])),
            ]);

    private static int PosicaoDe(ResultadoDaInvestigacao? veredito)
    {
        var posicao = Array.IndexOf(Vereditos, veredito);

        return posicao >= 0
            ? posicao
            : throw new ViolacaoDeInvariante($"Veredito fora do vocabulario: {veredito}.");
    }

    private static int FaixaDe(int score)
    {
        for (var indice = 1; indice < Bordas.Length; indice++)
        {
            if (score < Bordas[indice])
            {
                return indice - 1;
            }
        }

        throw new ViolacaoDeInvariante($"Score fora da faixa 0..100: {score}.");
    }

    private sealed class Acumulador
    {
        public int Total { get; private set; }

        public DistribuicaoDeDecisoes Vigente { get; private set; } = DistribuicaoDeDecisoes.Vazia;

        public DistribuicaoDeDecisoes Candidato { get; private set; } = DistribuicaoDeDecisoes.Vazia;

        public void Registrar(ObservacaoDoBacktest observacao)
        {
            Total++;
            Vigente = Vigente.Somar(observacao.DecisaoVigente);
            Candidato = Candidato.Somar(observacao.DecisaoCandidata);
        }
    }
}
