using Amazon.Lambda.SQSEvents;
using CentralAntifraude.Lambdas;

namespace CentralAntifraude.UnitTests.Lambdas;

/// <summary>
/// A tradução do evento da AWS para as mensagens que o produto conhece.
///
/// Parece plumbing, e tem duas decisões de verdade: qual identificador vira o
/// recibo, e o que fazer quando um atributo do SQS não vem.
/// </summary>
public sealed class LoteDoSqsTests
{
    private static SQSEvent Evento(Dictionary<string, string>? atributos = null) =>
        new()
        {
            Records =
            [
                new SQSEvent.SQSMessage
                {
                    MessageId = "id-da-mensagem",
                    ReceiptHandle = "handle-de-recibo",
                    Body = """{"eventId":"e-1"}""",
                    Attributes = atributos,
                },
            ],
        };

    /// <summary>
    /// O recibo é o `messageId`, e não o `receiptHandle`.
    ///
    /// **É a diferença entre reentregar a mensagem e perdê-la.** A API de falha
    /// parcial do Lambda casa por `messageId`; mandar o handle faz a AWS não
    /// reconhecer o item, e ela apaga a mensagem como se tivesse dado certo.
    /// Os dois são texto, então nada quebraria em tempo de compilação.
    /// </summary>
    [Fact]
    public void O_recibo_e_o_identificador_da_mensagem()
    {
        var mensagem = Assert.Single(LoteDoSqs.Ler(Evento()));

        Assert.Equal("id-da-mensagem", mensagem.Recibo);
    }

    [Fact]
    public void Le_a_contagem_de_entregas_e_o_horario_de_envio()
    {
        var mensagem = Assert.Single(LoteDoSqs.Ler(Evento(new()
        {
            ["ApproximateReceiveCount"] = "4",
            ["SentTimestamp"] = "1757332800000",
        })));

        Assert.Equal(4, mensagem.RecebimentosAproximados);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1757332800000),
            mensagem.InseridaEm);
    }

    /// <summary>
    /// Sem o atributo, a contagem é 1 — o lado seguro do erro.
    ///
    /// Subestimar apenas adia o redrive de uma mensagem ruim. Superestimar
    /// mandaria para a fila de mortas uma mensagem que ainda daria certo.
    /// </summary>
    [Fact]
    public void Sem_contagem_de_entregas_assume_a_primeira()
    {
        var mensagem = Assert.Single(LoteDoSqs.Ler(Evento()));

        Assert.Equal(1, mensagem.RecebimentosAproximados);
    }

    /// <summary>
    /// Sem o horário, a época zero — e não "agora".
    ///
    /// "Agora" produziria latência de fila igual a zero, que é um número
    /// plausível e falso. 1970 é obviamente errado, e um número obviamente
    /// errado é investigado; um número plausível e errado vira relatório.
    /// </summary>
    [Fact]
    public void Sem_horario_de_envio_usa_um_valor_visivelmente_absurdo()
    {
        var mensagem = Assert.Single(LoteDoSqs.Ler(Evento()));

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(0), mensagem.InseridaEm);
    }

    [Fact]
    public void Atributo_ilegivel_nao_derruba_a_leitura()
    {
        var mensagem = Assert.Single(LoteDoSqs.Ler(Evento(new()
        {
            ["ApproximateReceiveCount"] = "muitas",
            ["SentTimestamp"] = "ontem",
        })));

        Assert.Equal(1, mensagem.RecebimentosAproximados);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(0), mensagem.InseridaEm);
    }
}
