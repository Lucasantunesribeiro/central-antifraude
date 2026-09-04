namespace CentralAntifraude.Infrastructure.Mensageria;

/// <summary>
/// Uma mensagem esperando na fila.
///
/// Vive na Infrastructure, e nao no Domain, porque nao e um conceito da
/// Central Antifraude: e a simulacao de um servico externo. Quando a Fase 14
/// trocar esta fila pela SQS, esta classe e a tabela dela desaparecem sem que
/// o dominio perceba.
///
/// As propriedades sao de leitura para o codigo C#. Toda escrita passa por
/// <see cref="FilaEmPostgres"/>, em SQL: receber e esconder precisa acontecer
/// em um comando so, e um caminho paralelo de escrita pela entidade abriria a
/// janela em que dois consumidores recebem a mesma mensagem.
/// </summary>
public sealed class MensagemDaFila
{
    public Guid Id { get; private set; }

    public string Fila { get; private set; } = string.Empty;

    /// <summary>Envelope serializado. Para a fila, e texto opaco.</summary>
    public string Corpo { get; private set; } = string.Empty;

    /// <summary>Quando volta a ficar visivel. E o <c>VisibilityTimeout</c> do SQS.</summary>
    public DateTimeOffset DisponivelEm { get; private set; }

    /// <summary>Entregas ja feitas. E o <c>ApproximateReceiveCount</c> do SQS.</summary>
    public int Recebimentos { get; private set; }

    /// <summary>Identificador da entrega atual. E o <c>ReceiptHandle</c> do SQS.</summary>
    public Guid? Recibo { get; private set; }

    public DateTimeOffset InseridaEm { get; private set; }
}

/// <summary>
/// Uma mensagem que a politica de redrive tirou de circulacao.
///
/// Mantem o identificador original, para que a investigacao consiga ligar a
/// mensagem morta ao evento que a originou.
/// </summary>
public sealed class MensagemMorta
{
    public Guid Id { get; private set; }

    public string Fila { get; private set; } = string.Empty;

    public string Corpo { get; private set; } = string.Empty;

    public int Recebimentos { get; private set; }

    public string Motivo { get; private set; } = string.Empty;

    public DateTimeOffset InseridaEm { get; private set; }

    public DateTimeOffset MovidaEm { get; private set; }
}
