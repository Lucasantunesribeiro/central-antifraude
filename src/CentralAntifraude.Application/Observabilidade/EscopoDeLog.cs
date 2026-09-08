using System.Collections;
using System.Globalization;

namespace CentralAntifraude.Application.Observabilidade;

/// <summary>
/// As propriedades que acompanham um escopo de log, legíveis nos dois formatos.
///
/// **Por que não um <see cref="Dictionary{TKey, TValue}"/>.** Um dicionário
/// funciona perfeitamente no formato JSON — o provedor enumera os pares e
/// escreve cada um como propriedade. No console simples, porém, o provedor
/// chama <c>ToString()</c> no objeto do escopo, e um dicionário responde
/// <c>System.Collections.Generic.Dictionary`2[...]</c>. O resultado é que a
/// única informação que o escopo existia para carregar — o identificador de
/// correlação — sumia justamente na tela que alguém lê durante o
/// desenvolvimento.
///
/// Implementar <see cref="IReadOnlyList{T}"/> preserva o comportamento
/// estruturado; sobrescrever <see cref="ToString"/> resolve o texto.
/// </summary>
public sealed class EscopoDeLog : IReadOnlyList<KeyValuePair<string, object>>
{
    private readonly KeyValuePair<string, object>[] _itens;
    private readonly string _texto;

    public EscopoDeLog(params KeyValuePair<string, object>[] itens)
    {
        ArgumentNullException.ThrowIfNull(itens);

        _itens = itens;
        _texto = string.Join(
            ' ',
            itens.Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.Key}:{i.Value}")));
    }

    public int Count => _itens.Length;

    public KeyValuePair<string, object> this[int index] => _itens[index];

    /// <summary>Atalho para o caso mais comum: correlação, evento e tipo.</summary>
    public static EscopoDeLog DoEvento(string correlacao, Guid eventoId, string tipo) =>
        new(
            new KeyValuePair<string, object>("CorrelationId", correlacao),
            new KeyValuePair<string, object>("EventId", eventoId),
            new KeyValuePair<string, object>("EventType", tipo));

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, object>>)_itens).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _itens.GetEnumerator();

    public override string ToString() => _texto;
}
