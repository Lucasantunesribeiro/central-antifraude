using CentralAntifraude.Domain.Eventos;

namespace CentralAntifraude.Application.Mensageria;

/// <summary>
/// Uma mensagem recebida da fila, com os metadados de entrega.
///
/// <see cref="Recibo"/> e o que identifica ESTA entrega, e nao a mensagem: e
/// com ele que o consumidor apaga a mensagem depois de processar. E o
/// equivalente do <c>ReceiptHandle</c> do SQS, e existe pela mesma razao — a
/// mesma mensagem pode ser entregue de novo, com recibo diferente.
/// </summary>
public sealed record MensagemRecebida(
    string Recibo,
    string Corpo,
    int RecebimentosAproximados,
    DateTimeOffset InseridaEm);

/// <summary>
/// A fila operacional, na forma que o resto do sistema conhece.
///
/// **O contrato e o do SQS Standard**, de propósito (CLAUDE.md secao 31):
///
/// - entrega **ao menos uma vez** — a mesma mensagem pode chegar duas vezes;
/// - **sem ordem garantida** — nada pode depender da sequencia de chegada;
/// - **visibilidade temporaria** — receber esconde a mensagem por um tempo; se
///   ninguem apagar dentro desse prazo, ela volta para a fila;
/// - **redrive** — depois de N recebimentos sem apagar, a mensagem vai para a
///   DLQ em vez de circular para sempre.
///
/// Programar contra este contrato e o que impede o sistema de depender de
/// garantias que o SQS nao da. Uma implementacao que entregasse exatamente uma
/// vez e em ordem faria os testes passarem e esconderia o defeito ate a
/// producao.
/// </summary>
public interface IFilaDeMensagens
{
    /// <summary>Nome da fila operacional de eventos de dominio.</summary>
    const string FilaOperacional = "eventos-operacionais";

    /// <summary>
    /// Nome da fila de backtests.
    ///
    /// Separada desde ja (CLAUDE.md secao 30): backtest e trabalho longo e em
    /// lote, e nao pode ficar na frente de um alerta operacional na mesma fila.
    /// A Fase 9 a utiliza.
    /// </summary>
    const string FilaDeBacktests = "backtests";

    Task EnviarAsync(string fila, string corpo, CancellationToken cancellationToken);

    /// <summary>
    /// Recebe ate <paramref name="quantidade"/> mensagens e as esconde pelo
    /// tempo de visibilidade configurado.
    ///
    /// Esconder, e nao remover: se o processo morrer no meio, a mensagem volta
    /// sozinha. Remover na leitura transformaria qualquer queda em perda.
    /// </summary>
    Task<IReadOnlyList<MensagemRecebida>> ReceberAsync(
        string fila,
        int quantidade,
        CancellationToken cancellationToken);

    /// <summary>Apaga a mensagem. So depois disto ela deixa de existir.</summary>
    Task ApagarAsync(string recibo, CancellationToken cancellationToken);

    /// <summary>
    /// Devolve a mensagem para a fila imediatamente, sem esperar a
    /// visibilidade expirar.
    ///
    /// Usado quando o consumidor sabe que nao vai conseguir agora mas a
    /// mensagem continua valida — uma indisponibilidade temporaria, por
    /// exemplo. Contabiliza o recebimento normalmente.
    /// </summary>
    Task DevolverAsync(string recibo, CancellationToken cancellationToken);

    /// <summary>Quantas mensagens esperam na fila. Somente para diagnostico e teste.</summary>
    Task<int> ContarPendentesAsync(string fila, CancellationToken cancellationToken);

    /// <summary>Quantas mensagens estao na fila de mortas. Somente para diagnostico e teste.</summary>
    Task<int> ContarMortasAsync(string fila, CancellationToken cancellationToken);
}

/// <summary>Parametros da fila, espelhando os do SQS Standard.</summary>
public sealed class OpcoesDaFila
{
    public const string Secao = "Fila";

    /// <summary>
    /// Por quanto tempo uma mensagem recebida fica escondida.
    ///
    /// Precisa ser confortavelmente maior do que o processamento mais lento:
    /// se expirar antes do fim, outro consumidor recebe a mesma mensagem e o
    /// efeito e tentado duas vezes em paralelo. A Inbox impede o dano, mas o
    /// trabalho e jogado fora.
    /// </summary>
    public int VisibilidadeEmSegundos { get; set; } = 30;

    /// <summary>
    /// Quantas vezes uma mensagem pode ser recebida antes de ir para a DLQ.
    ///
    /// E o <c>maxReceiveCount</c> da politica de redrive. Sem ele, uma
    /// mensagem que sempre falha circula para sempre, consome o worker e
    /// esconde as mensagens boas atras dela.
    /// </summary>
    public int MaximoDeRecebimentos { get; set; } = 5;

    /// <summary>Quantas mensagens o worker pega por ciclo.</summary>
    public int TamanhoDoLote { get; set; } = 10;

    public TimeSpan Visibilidade => TimeSpan.FromSeconds(VisibilidadeEmSegundos);

    public void Validar()
    {
        if (VisibilidadeEmSegundos is < 1 or > 43_200)
        {
            // 12 horas e o teto do proprio SQS. Aceitar mais aqui criaria uma
            // configuracao que nao sobrevive a Fase 14.
            throw new InvalidOperationException(
                $"{Secao}:VisibilidadeEmSegundos deve estar entre 1 e 43200.");
        }

        if (MaximoDeRecebimentos is < 1 or > 1_000)
        {
            throw new InvalidOperationException(
                $"{Secao}:MaximoDeRecebimentos deve estar entre 1 e 1000.");
        }

        if (TamanhoDoLote is < 1 or > 10)
        {
            // 10 e o teto de ReceiveMessage do SQS. Mesmo motivo.
            throw new InvalidOperationException($"{Secao}:TamanhoDoLote deve estar entre 1 e 10.");
        }
    }
}

/// <summary>Resultado de um ciclo do despachante da Outbox.</summary>
public sealed record ResultadoDoDespacho(int Publicados, int Falhados)
{
    public int Total => Publicados + Falhados;
}

/// <summary>Resultado de um ciclo do worker.</summary>
public sealed record ResultadoDoConsumo(int Processados, int Repetidos, int Recusados, int Falhados)
{
    /// <summary>Mensagens que o worker tirou da fila, por qualquer motivo.</summary>
    public int Total => Processados + Repetidos + Recusados + Falhados;
}
