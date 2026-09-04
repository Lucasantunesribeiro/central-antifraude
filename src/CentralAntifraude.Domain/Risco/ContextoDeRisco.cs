namespace CentralAntifraude.Domain.Risco;

/// <summary>
/// Uma transacao anterior do mesmo cliente, reduzida ao que as regras usam.
///
/// Nao e a entidade <c>Transacao</c>: e uma projecao. Carregar entidades
/// completas traria colunas que nenhuma regra le, e amarraria o motor ao
/// formato de persistencia — o que atrapalharia o backtest da Fase 9, que
/// monta o mesmo contexto a partir de outra fonte.
/// </summary>
public sealed record TransacaoDoHistorico(
    DateTimeOffset OcorridaEm,
    decimal Valor,
    string Moeda,
    string? FingerprintDoDispositivo,
    string? PaisDeOrigem);

/// <summary>
/// Tudo o que as regras podem saber sobre o passado, carregado de uma vez.
///
/// **Por que um contexto, e nao consultas dentro de cada regra**
/// (ROADMAP secao 3.5):
///
/// - **determinismo** — todas as regras enxergam exatamente o mesmo passado,
///   no mesmo instante. Se cada uma consultasse por conta propria, duas
///   regras poderiam discordar sobre o historico dentro da mesma avaliacao;
/// - **teste** — o motor inteiro roda sem banco;
/// - **desempenho** — uma consulta por avaliacao, e nao uma por regra;
/// - **backtest** — a Fase 9 precisa rodar o MESMO motor sobre dados
///   historicos. Com o contexto explicito, basta monta-lo de outra forma.
///
/// O historico ja chega filtrado pelo cliente e pelo tenant, e ordenado da
/// mais recente para a mais antiga.
/// </summary>
public sealed class ContextoDeRisco
{
    private readonly IReadOnlyList<TransacaoDoHistorico> _historico;

    public ContextoDeRisco(IReadOnlyList<TransacaoDoHistorico> historicoDoCliente)
    {
        ArgumentNullException.ThrowIfNull(historicoDoCliente);

        _historico = historicoDoCliente;
    }

    /// <summary>Contexto de um cliente sem nenhuma transacao anterior.</summary>
    public static ContextoDeRisco Vazio { get; } = new([]);

    /// <summary>Transacoes anteriores do cliente, da mais recente para a mais antiga.</summary>
    public IReadOnlyList<TransacaoDoHistorico> Historico => _historico;

    public int TotalDeTransacoes => _historico.Count;

    public bool SemHistorico => _historico.Count == 0;

    /// <summary>
    /// Quantas transacoes anteriores caem na janela que termina em
    /// <paramref name="referencia"/>.
    ///
    /// A janela e ancorada em <c>OcorridaEm</c> da transacao avaliada, e nao
    /// no relogio do servidor. Um evento que chega atrasado precisa ser
    /// medido contra os vizinhos DELE — usar "agora" faria uma rajada de
    /// ontem parecer isolada so porque demorou a chegar.
    ///
    /// O intervalo e semiaberto: <c>(referencia - janela, referencia]</c>.
    /// </summary>
    public int QuantidadeNaJanela(DateTimeOffset referencia, TimeSpan janela)
    {
        var inicio = referencia - janela;

        return _historico.Count(t => t.OcorridaEm > inicio && t.OcorridaEm <= referencia);
    }

    /// <summary>
    /// O cliente ja usou este dispositivo antes?
    ///
    /// Fingerprint ausente devolve <c>true</c> de proposito: sem o dado, nao
    /// ha como afirmar que o dispositivo e novo, e afirmar mesmo assim
    /// geraria sinal de risco a partir de ignorancia.
    /// </summary>
    public bool DispositivoConhecido(string? fingerprint) =>
        string.IsNullOrWhiteSpace(fingerprint) ||
        _historico.Any(t => string.Equals(t.FingerprintDoDispositivo, fingerprint, StringComparison.Ordinal));

    /// <summary>
    /// O cliente ja transacionou deste pais antes?
    /// Mesma regra do dispositivo: sem o dado, nao ha divergencia a apontar.
    /// </summary>
    public bool PaisConhecido(string? pais) =>
        string.IsNullOrWhiteSpace(pais) ||
        _historico.Any(t => string.Equals(t.PaisDeOrigem, pais, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Valor medio das transacoes anteriores na mesma moeda.
    ///
    /// A moeda importa: comparar 100 BRL com uma media calculada sobre USD
    /// produziria um sinal sem significado. Sem historico na moeda, devolve
    /// nulo e a regra se cala.
    /// </summary>
    public decimal? ValorMedio(string moeda)
    {
        var naMoeda = _historico
            .Where(t => string.Equals(t.Moeda, moeda, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Valor)
            .ToList();

        return naMoeda.Count == 0 ? null : decimal.Round(naMoeda.Average(), 4);
    }

    /// <summary>Quantas transacoes anteriores existem na moeda informada.</summary>
    public int TotalNaMoeda(string moeda) =>
        _historico.Count(t => string.Equals(t.Moeda, moeda, StringComparison.OrdinalIgnoreCase));

    /// <summary>Paises distintos ja vistos, em ordem estavel.</summary>
    public IReadOnlyList<string> PaisesConhecidos =>
        _historico
            .Select(t => t.PaisDeOrigem)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
}
