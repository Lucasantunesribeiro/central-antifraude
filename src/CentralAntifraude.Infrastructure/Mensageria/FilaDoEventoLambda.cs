using CentralAntifraude.Application.Mensageria;

namespace CentralAntifraude.Infrastructure.Mensageria;

/// <summary>
/// A ponte entre o contrato de fila do produto e o modelo de ack do Lambda.
///
/// **O problema que ela resolve.** `ProcessadorDeEventos` foi escrito na Fase 5
/// contra um modelo explicito: receber, aplicar o efeito, apagar. O SQS com
/// gatilho de Lambda inverte os dois extremos — a AWS entrega as mensagens no
/// evento, e apaga sozinha o que a invocacao nao reportar como falha.
///
/// Havia duas saidas. Reescrever o consumidor para o modelo do Lambda, ou
/// adaptar o modelo do Lambda ao contrato que ja existe. A segunda foi
/// escolhida porque o consumidor carrega tudo o que dez fases construiram —
/// Inbox, savepoint por efeito, conferencia de tenant, redrive — e reescreve-lo
/// significaria reprovar essas garantias num codigo novo.
///
/// Entao o adaptador finge ser uma fila:
///
/// | Chamada do consumidor | O que acontece aqui |
/// |---|---|
/// | `ReceberAsync` | devolve as mensagens que vieram no evento, uma vez |
/// | `ApagarAsync` | nada — a AWS apaga ao fim da invocacao |
/// | `DevolverAsync` | anota o recibo em <see cref="RecibosComFalha"/> |
/// | `EnviarAsync` | delega ao SQS de verdade |
///
/// No fim, o handler devolve os recibos anotados como `batchItemFailures`, e a
/// AWS reentrega **apenas aquelas** mensagens. Sem isso, uma mensagem ruim no
/// meio de nove boas faria as nove voltarem — a Inbox tornaria isso inofensivo,
/// mas inofensivo nao e de graca.
///
/// **Nao e thread-safe, e nao precisa ser.** Uma instancia por invocacao, e o
/// consumidor trata as mensagens em sequencia.
/// </summary>
public sealed class FilaDoEventoLambda : IFilaDeMensagens
{
    private readonly IFilaDeMensagens _envio;
    private readonly Queue<MensagemRecebida> _pendentes;
    private readonly HashSet<string> _entregues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _confirmados = new(StringComparer.Ordinal);

    public FilaDoEventoLambda(IFilaDeMensagens envio, IEnumerable<MensagemRecebida> mensagens)
    {
        ArgumentNullException.ThrowIfNull(mensagens);

        _envio = envio;
        _pendentes = new Queue<MensagemRecebida>(mensagens);
    }

    /// <summary>
    /// O que NAO foi confirmado — vira `batchItemFailures`.
    ///
    /// **A conta e por subtracao, e isso resolve um caso que a soma nao
    /// resolveria.** O consumidor confirma o sucesso (`ApagarAsync`) e devolve
    /// o que recusa (`DevolverAsync`), mas NAO devolve o que falha por erro
    /// transitorio: ali ele deixa a visibilidade expirar, que e o certo quando
    /// a fila e uma tabela. Contar apenas as devolucoes deixaria essas
    /// mensagens sem reporte, e a AWS as apagaria como se tivessem dado certo —
    /// perda silenciosa, que e o pior desfecho possivel.
    ///
    /// Entregue e nao confirmado significa "nao terminou bem", qualquer que
    /// tenha sido o motivo. O recibo e o `messageId` do SQS, que e o
    /// identificador que a API de falha parcial espera.
    /// </summary>
    public IReadOnlySet<string> RecibosComFalha =>
        _entregues.Except(_confirmados).ToHashSet(StringComparer.Ordinal);

    public Task EnviarAsync(string fila, string corpo, CancellationToken cancellationToken) =>
        _envio.EnviarAsync(fila, corpo, cancellationToken);

    /// <summary>
    /// Entrega o lote do evento, e depois nada.
    ///
    /// A segunda chamada devolve vazio de proposito: o consumidor foi escrito
    /// para um laco que continua ate a fila esvaziar, e numa invocacao de
    /// Lambda "a fila" e exatamente o que veio no evento.
    /// </summary>
    public Task<IReadOnlyList<MensagemRecebida>> ReceberAsync(
        string fila,
        int quantidade,
        CancellationToken cancellationToken)
    {
        var lote = new List<MensagemRecebida>(Math.Min(quantidade, _pendentes.Count));

        while (lote.Count < quantidade && _pendentes.Count > 0)
        {
            var mensagem = _pendentes.Dequeue();

            _entregues.Add(mensagem.Recibo);
            lote.Add(mensagem);
        }

        return Task.FromResult<IReadOnlyList<MensagemRecebida>>(lote);
    }

    /// <summary>
    /// Confirmacao e ausencia de falha: nao ha nada a fazer.
    ///
    /// Chamar a API de apagar aqui seria pior do que inutil — a mensagem ja
    /// sera apagada pela AWS, e a chamada extra custaria uma requisicao SQS
    /// por mensagem processada.
    /// </summary>
    public Task ApagarAsync(string recibo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recibo);

        // Nao ha chamada a AWS: ela apaga sozinha o que nao for reportado como
        // falha. O que se registra aqui e o CONSENTIMENTO — "esta pode ir".
        _confirmados.Add(recibo);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Recusa explicita. Nao precisa registrar nada: ausencia de confirmacao
    /// ja e falha, e devolver e uma das formas de nao confirmar.
    /// </summary>
    public Task DevolverAsync(string recibo, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<int> ContarPendentesAsync(string fila, CancellationToken cancellationToken) =>
        _envio.ContarPendentesAsync(fila, cancellationToken);

    public Task<int> ContarMortasAsync(string fila, CancellationToken cancellationToken) =>
        _envio.ContarMortasAsync(fila, cancellationToken);
}
