using System.Globalization;
using Amazon.Lambda.SQSEvents;
using CentralAntifraude.Application.Mensageria;

namespace CentralAntifraude.Lambdas;

/// <summary>
/// Traduz o evento que a AWS entrega para as mensagens que o produto conhece.
///
/// Os dois consumidores — eventos e backtests — precisam exatamente da mesma
/// tradução, e ela tem duas decisões que não são óbvias o bastante para ficarem
/// escritas em dois lugares.
/// </summary>
internal static class LoteDoSqs
{
    public static IEnumerable<MensagemRecebida> Ler(SQSEvent evento) =>
        evento.Records.Select(registro => new MensagemRecebida(
            // O identificador da mensagem, e NÃO o handle de recibo: é ele que
            // a API de falha parcial espera de volta em `batchItemFailures`.
            registro.MessageId,
            registro.Body,
            ContagemDeEntregas(registro),
            HorarioDeEnvio(registro)));

    /// <summary>
    /// Quantas vezes o SQS já entregou esta mensagem.
    ///
    /// O atributo chega como texto e pode faltar em evento montado à mão. Aí 1
    /// é a suposição segura: subestimar apenas adia o redrive, enquanto
    /// superestimar descartaria cedo demais uma mensagem que ainda daria certo.
    /// </summary>
    private static int ContagemDeEntregas(SQSEvent.SQSMessage registro) =>
        registro.Attributes is not null &&
        registro.Attributes.TryGetValue("ApproximateReceiveCount", out var texto) &&
        int.TryParse(texto, CultureInfo.InvariantCulture, out var valor)
            ? valor
            : 1;

    /// <summary>
    /// Quando a mensagem entrou na fila, em época Unix de milissegundos.
    ///
    /// Na ausência do atributo, a época zero: é um instante absurdamente antigo,
    /// e um valor absurdo na medida de latência é infinitamente preferível a
    /// `agora`, que seria plausível e mentiroso.
    /// </summary>
    private static DateTimeOffset HorarioDeEnvio(SQSEvent.SQSMessage registro) =>
        registro.Attributes is not null &&
        registro.Attributes.TryGetValue("SentTimestamp", out var texto) &&
        long.TryParse(texto, CultureInfo.InvariantCulture, out var epoch)
            ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
            : DateTimeOffset.FromUnixTimeMilliseconds(0);
}
