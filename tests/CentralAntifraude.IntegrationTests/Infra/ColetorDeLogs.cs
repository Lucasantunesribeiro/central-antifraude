using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace CentralAntifraude.IntegrationTests.Infra;

/// <summary>
/// Uma linha de log capturada, com tudo o que o escopo carregava.
/// </summary>
public sealed record EntradaDeLog(
    LogLevel Nivel,
    string Categoria,
    int EventId,
    string Mensagem,
    IReadOnlyDictionary<string, string> Propriedades)
{
    /// <summary>Valor de uma propriedade estruturada, ou <c>null</c>.</summary>
    public string? Propriedade(string nome) =>
        Propriedades.TryGetValue(nome, out var valor) ? valor : null;
}

/// <summary>
/// Provedor de log em memoria que enxerga ESCOPOS.
///
/// **Por que isto precisou existir na Fase 12.** O provedor anterior devolvia
/// <c>null</c> em <c>BeginScope</c>. Funcionava para o que se pedia dele — ler
/// o texto de uma mensagem de erro —, mas tornava impossivel afirmar a coisa
/// mais importante sobre um log estruturado: que a linha carrega as
/// propriedades certas. Um teste de correlacao escrito sobre aquele provedor
/// so poderia procurar o identificador dentro do TEXTO da mensagem, e passaria
/// a mentir no dia em que a correlacao passasse a viajar por escopo — que e
/// exatamente o que ela faz agora.
///
/// Aqui as propriedades do escopo e as do estado sao combinadas, com o estado
/// por ultimo: uma linha que declare explicitamente um valor vence o que o
/// escopo herdou.
/// </summary>
public sealed class ColetorDeLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _texto = new();
    private readonly ConcurrentQueue<EntradaDeLog> _entradas = new();
    private readonly AsyncLocal<Escopo?> _escopoAtual = new();

    /// <summary>Tudo o que foi registrado, na ordem.</summary>
    public IReadOnlyList<EntradaDeLog> Entradas => [.. _entradas];

    /// <summary>
    /// As mesmas linhas em texto puro, como uma FOTOGRAFIA.
    ///
    /// Devolver uma copia nao e zelo excessivo — e correcao. Desde que existe
    /// uma linha de log por requisicao, ha sempre alguma thread escrevendo
    /// enquanto o teste le, e enumerar a lista viva estourava
    /// `Collection was modified`. O sintoma aparecia longe da causa: em testes
    /// de console que so queriam montar uma mensagem de falha.
    /// </summary>
    public IReadOnlyList<string> Texto => [.. _texto];

    public ILogger CreateLogger(string categoryName) => new LogEmMemoria(this, categoryName);

    public void Dispose()
    {
    }

    private IDisposable Empilhar(IEnumerable<KeyValuePair<string, object?>> itens)
    {
        var escopo = new Escopo(this, _escopoAtual.Value, itens);
        _escopoAtual.Value = escopo;
        return escopo;
    }

    private Dictionary<string, string> PropriedadesDoEscopo()
    {
        var acumulado = new Dictionary<string, string>(StringComparer.Ordinal);

        // Do mais externo para o mais interno: o escopo de dentro sobrepoe o de
        // fora, que e a semantica que qualquer provedor de verdade aplica.
        var pilha = new Stack<Escopo>();

        for (var atual = _escopoAtual.Value; atual is not null; atual = atual.Pai)
        {
            pilha.Push(atual);
        }

        while (pilha.Count > 0)
        {
            foreach (var (chave, valor) in pilha.Pop().Itens)
            {
                acumulado[chave] = Convert.ToString(valor, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        return acumulado;
    }

    private sealed class Escopo : IDisposable
    {
        private readonly ColetorDeLogs _dono;

        public Escopo(
            ColetorDeLogs dono,
            Escopo? pai,
            IEnumerable<KeyValuePair<string, object?>> itens)
        {
            _dono = dono;
            Pai = pai;
            Itens = [.. itens];
        }

        public Escopo? Pai { get; }

        public IReadOnlyList<KeyValuePair<string, object?>> Itens { get; }

        public void Dispose() => _dono._escopoAtual.Value = Pai;
    }

    private sealed class LogEmMemoria : ILogger
    {
        private readonly ColetorDeLogs _dono;
        private readonly string _categoria;

        public LogEmMemoria(ColetorDeLogs dono, string categoria)
        {
            _dono = dono;
            _categoria = categoria;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            state is IEnumerable<KeyValuePair<string, object?>> itens
                ? _dono.Empilhar(itens)
                : null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var texto = formatter(state, exception);
            var linha = $"[{logLevel}] {_categoria}: {texto}";

            if (exception is not null)
            {
                linha += $" || {exception.GetType().Name}: {exception.Message}";
            }

            var propriedades = _dono.PropriedadesDoEscopo();

            if (state is IEnumerable<KeyValuePair<string, object?>> itens)
            {
                foreach (var (chave, valor) in itens)
                {
                    propriedades[chave] = Convert.ToString(valor, CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }

            _dono._entradas.Enqueue(
                new EntradaDeLog(logLevel, _categoria, eventId.Id, texto, propriedades));

            _dono._texto.Enqueue(linha);
        }
    }
}
