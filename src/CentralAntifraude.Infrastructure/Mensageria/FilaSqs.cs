using Amazon.SQS;
using Amazon.SQS.Model;
using CentralAntifraude.Application.Mensageria;

namespace CentralAntifraude.Infrastructure.Mensageria;

/// <summary>Nomes das filas reais, lidos da configuracao no deploy.</summary>
public sealed class OpcoesDaFilaSqs
{
    public const string Secao = "Mensageria";

    /// <summary>URL da fila operacional. Vazio significa "nao estamos na AWS".</summary>
    public string FilaOperacional { get; set; } = string.Empty;

    public string FilaDeBacktests { get; set; } = string.Empty;

    /// <summary>Nome da funcao Lambda do despachante, para o disparo imediato.</summary>
    public string FuncaoDoDespachante { get; set; } = string.Empty;

    /// <summary>
    /// Ha configuracao de AWS suficiente para usar SQS de verdade?
    ///
    /// A composicao decide por ISTO, e nao por uma variavel do tipo
    /// `Ambiente == "Producao"`. O criterio precisa ser "existe uma fila para
    /// onde mandar", porque e exatamente essa a pergunta; um nome de ambiente
    /// e uma aproximacao que erra no dia em que alguem criar um quarto
    /// ambiente.
    /// </summary>
    public bool UsaSqs => !string.IsNullOrWhiteSpace(FilaOperacional);

    /// <summary>Traduz o nome logico da fila para a URL real.</summary>
    public string UrlDe(string fila) =>
        fila switch
        {
            IFilaDeMensagens.FilaDeBacktests => FilaDeBacktests,
            _ => FilaOperacional,
        };
}

/// <summary>
/// A fila operacional em SQS de verdade.
///
/// **O que muda em relacao a <see cref="FilaEmPostgres"/>, e o que NAO muda.**
///
/// Nao muda o contrato: quem envia continua chamando `EnviarAsync`, e o
/// despachante da Outbox nao sabe de qual das duas esta falando. Era esse o
/// ponto de existir uma interface desde a Fase 5.
///
/// Muda quem faz o polling. No SQS com gatilho de Lambda, **a AWS recebe as
/// mensagens e invoca a funcao** — o worker nunca chama `ReceberAsync`. Por
/// isso as operacoes de recebimento aqui lancam: chama-las significaria que
/// alguem montou o worker do jeito errado, e falhar alto e melhor do que
/// devolver lista vazia e deixar a fila crescer em silencio.
///
/// O ack tambem inverte. Em PostgreSQL, apagar e uma escrita nossa; no Lambda,
/// a AWS apaga a mensagem quando a invocacao termina sem reportar falha. Quem
/// faz essa ponte e a <see cref="FilaDoEventoLambda"/>, e nao esta classe.
/// </summary>
public sealed class FilaSqs : IFilaDeMensagens
{
    private readonly IAmazonSQS _sqs;
    private readonly OpcoesDaFilaSqs _opcoes;

    public FilaSqs(IAmazonSQS sqs, OpcoesDaFilaSqs opcoes)
    {
        _sqs = sqs;
        _opcoes = opcoes;
    }

    public Task EnviarAsync(string fila, string corpo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fila);
        ArgumentException.ThrowIfNullOrWhiteSpace(corpo);

        return _sqs.SendMessageAsync(
            new SendMessageRequest { QueueUrl = _opcoes.UrlDe(fila), MessageBody = corpo },
            cancellationToken);
    }

    public Task<IReadOnlyList<MensagemRecebida>> ReceberAsync(
        string fila,
        int quantidade,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "Em SQS com gatilho de Lambda quem recebe e a AWS: o worker e invocado com " +
            "as mensagens no evento. Se este metodo foi chamado, o consumidor foi montado " +
            "com a fila errada.");

    public Task ApagarAsync(string recibo, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "A AWS apaga a mensagem quando a invocacao termina sem reportar falha.");

    public Task DevolverAsync(string recibo, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "Devolver e reportar falha do item no retorno da invocacao.");

    /// <summary>
    /// Contagem aproximada, que e a unica que o SQS oferece.
    ///
    /// "Aproximada" nao e ressalva de documentacao: numa fila distribuida o
    /// numero exato nao existe em instante nenhum. Serve para medir tendencia,
    /// e nao para conferir saldo.
    /// </summary>
    public async Task<int> ContarPendentesAsync(string fila, CancellationToken cancellationToken)
    {
        var resposta = await _sqs.GetQueueAttributesAsync(
            new GetQueueAttributesRequest
            {
                QueueUrl = _opcoes.UrlDe(fila),
                AttributeNames = ["ApproximateNumberOfMessages"],
            },
            cancellationToken);

        return resposta.ApproximateNumberOfMessages;
    }

    /// <summary>
    /// Quantas mensagens estao na fila de mortas.
    ///
    /// O redrive e configurado na infraestrutura, e a fila de mortas tem o
    /// nome da fila de origem com o sufixo `-mortas`. Derivar o nome aqui evita
    /// mais duas variaveis de configuracao para dizer o que o template ja diz.
    /// </summary>
    public async Task<int> ContarMortasAsync(string fila, CancellationToken cancellationToken)
    {
        var url = _opcoes.UrlDe(fila) + "-mortas";

        try
        {
            var resposta = await _sqs.GetQueueAttributesAsync(
                new GetQueueAttributesRequest
                {
                    QueueUrl = url,
                    AttributeNames = ["ApproximateNumberOfMessages"],
                },
                cancellationToken);

            return resposta.ApproximateNumberOfMessages;
        }
        catch (QueueDoesNotExistException)
        {
            // Fila de mortas ausente e configuracao incompleta, e nao motivo
            // para derrubar a amostragem de indicadores: quem chama isto e o
            // termometro, e termometro quebrado nao pode parar a operacao.
            return 0;
        }
    }
}
