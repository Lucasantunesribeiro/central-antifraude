using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using CentralAntifraude.Application.Observabilidade;

namespace CentralAntifraude.IntegrationTests.Infra;

/// <summary>Uma medicao, com as dimensoes que ela carregava.</summary>
public sealed record MedidaColetada(
    string Medidor,
    string Instrumento,
    double Valor,
    IReadOnlyDictionary<string, string> Etiquetas);

/// <summary>
/// Escuta as metricas que o processo emite de verdade.
///
/// **Por que nao existe rota de metricas neste produto.** A alternativa obvia
/// seria um endpoint que devolvesse os numeros, e ela foi recusada por dois
/// motivos. O primeiro e que profundidade de Outbox e de fila sao numeros do
/// PROCESSO, e nao de uma organizacao: nao ha como devolve-los a um
/// administrador de um tenant sem contar a ele o volume de trabalho dos outros.
/// O segundo e que a rota nao seria necessaria — o teste roda no mesmo processo
/// que a API e escuta os instrumentos direto, e a Fase 14 pluga um exportador
/// nos mesmos medidores. Uma superficie HTTP a mais nao provaria nada que isto
/// nao prove, e teria que ser autorizada, limitada e defendida.
///
/// O <c>MeterListener</c> enxerga inclusive os instrumentos criados antes de
/// <c>Start()</c>: eles sao estaticos e nascem no primeiro uso da classe.
/// </summary>
public sealed class ColetorDeMetricas : IDisposable
{
    private readonly MeterListener _ouvinte;
    private readonly ConcurrentQueue<MedidaColetada> _medidas = new();

    public ColetorDeMetricas()
    {
        _ouvinte = new MeterListener
        {
            InstrumentPublished = (instrumento, ouvinte) =>
            {
                if (instrumento.Meter.Name.StartsWith(Telemetria.Prefixo, StringComparison.Ordinal))
                {
                    ouvinte.EnableMeasurementEvents(instrumento);
                }
            },
        };

        _ouvinte.SetMeasurementEventCallback<long>(
            (instrumento, valor, etiquetas, _) => Registrar(instrumento, valor, etiquetas));

        _ouvinte.SetMeasurementEventCallback<int>(
            (instrumento, valor, etiquetas, _) => Registrar(instrumento, valor, etiquetas));

        _ouvinte.SetMeasurementEventCallback<double>(
            (instrumento, valor, etiquetas, _) => Registrar(instrumento, valor, etiquetas));

        _ouvinte.Start();
    }

    public IReadOnlyList<MedidaColetada> Medidas => [.. _medidas];

    /// <summary>Todas as medicoes de um instrumento, filtradas por dimensao.</summary>
    public IReadOnlyList<MedidaColetada> De(string instrumento, string? etiqueta = null, string? valor = null) =>
        [.. _medidas.Where(m =>
            string.Equals(m.Instrumento, instrumento, StringComparison.Ordinal) &&
            (etiqueta is null ||
             (m.Etiquetas.TryGetValue(etiqueta, out var lido) &&
              string.Equals(lido, valor, StringComparison.Ordinal))))];

    /// <summary>Soma das medicoes — o valor de um contador.</summary>
    public double Soma(string instrumento, string? etiqueta = null, string? valor = null) =>
        De(instrumento, etiqueta, valor).Sum(m => m.Valor);

    /// <summary>Quantas medicoes existem — o tamanho da amostra de um histograma.</summary>
    public int Quantidade(string instrumento, string? etiqueta = null, string? valor = null) =>
        De(instrumento, etiqueta, valor).Count;

    /// <summary>Ultimo valor observado — a leitura de um medidor de estado.</summary>
    public double? Ultimo(string instrumento, string? etiqueta = null, string? valor = null) =>
        De(instrumento, etiqueta, valor) is { Count: > 0 } medidas
            ? medidas[^1].Valor
            : null;

    /// <summary>Todo nome de dimensao que apareceu em alguma medicao.</summary>
    public IReadOnlySet<string> DimensoesUsadas() =>
        _medidas
            .SelectMany(m => m.Etiquetas.Keys)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Todo nome de instrumento que emitiu alguma medicao.</summary>
    public IReadOnlySet<string> InstrumentosUsados() =>
        _medidas
            .Select(m => m.Instrumento)
            .ToHashSet(StringComparer.Ordinal);

    public void Dispose() => _ouvinte.Dispose();

    private void Registrar(
        Instrument instrumento,
        double valor,
        ReadOnlySpan<KeyValuePair<string, object?>> etiquetas)
    {
        var dimensoes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (chave, conteudo) in etiquetas)
        {
            dimensoes[chave] = Convert.ToString(conteudo, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        _medidas.Enqueue(
            new MedidaColetada(instrumento.Meter.Name, instrumento.Name, valor, dimensoes));
    }
}
