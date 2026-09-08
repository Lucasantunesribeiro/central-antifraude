using Amazon.Lambda;
using Amazon.Lambda.Model;
using CentralAntifraude.Application.Mensageria;
using Microsoft.Extensions.Logging;

namespace CentralAntifraude.Infrastructure.Mensageria;

/// <summary>
/// Fora da AWS, ninguem precisa ser acordado.
///
/// Em desenvolvimento e nos testes o despachante e um laco em processo que
/// roda a cada dois segundos, ou e chamado a mao pelo proprio teste. Registrar
/// uma implementacao vazia — em vez de deixar a dependencia opcional — mantem
/// o caminho de chamada identico nos tres ambientes: o codigo da ingestao nao
/// tem `if`, e o teste exercita a mesma sequencia que producao executa.
/// </summary>
public sealed class DespachanteImediatoInerte : IDespachanteImediato
{
    public Task AcordarAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Invoca a funcao Lambda do despachante, sem esperar por ela.
///
/// **`InvocationType = Event` e a decisao inteira.** Com `RequestResponse`, a
/// requisicao do integrador ficaria presa esperando o despacho terminar — e o
/// despacho pode levar segundos, porque acorda o banco e publica um lote. O
/// integrador ja tem a resposta dele: a transacao foi avaliada e a decisao
/// esta pronta. O que acontece depois nao e problema dele.
///
/// A falha nunca sobe. Ver <see cref="IDespachanteImediato"/>: este caminho e
/// otimizacao de latencia, e a garantia de entrega continua sendo a Outbox mais
/// a varredura periodica.
/// </summary>
public sealed partial class DespachanteImediatoEmLambda : IDespachanteImediato
{
    private readonly IAmazonLambda _lambda;
    private readonly OpcoesDaFilaSqs _opcoes;
    private readonly ILogger<DespachanteImediatoEmLambda> _log;

    public DespachanteImediatoEmLambda(
        IAmazonLambda lambda,
        OpcoesDaFilaSqs opcoes,
        ILogger<DespachanteImediatoEmLambda> log)
    {
        _lambda = lambda;
        _opcoes = opcoes;
        _log = log;
    }

    public async Task AcordarAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_opcoes.FuncaoDoDespachante))
        {
            return;
        }

        try
        {
            await _lambda.InvokeAsync(
                new InvokeRequest
                {
                    FunctionName = _opcoes.FuncaoDoDespachante,
                    InvocationType = InvocationType.Event,
                    Payload = """{"origem":"api"}""",
                },
                cancellationToken);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            // Engolida de proposito. A transacao ja foi confirmada, e derrubar
            // a resposta agora transformaria um atraso de ate quinze minutos
            // num erro para o integrador — que reenviaria a mesma transacao,
            // com o mesmo resultado, e o mesmo aviso falhando de novo.
            RegistrarFalhaAoAcordar(_log, excecao);
        }
    }

    [LoggerMessage(
        EventId = 550,
        Level = LogLevel.Warning,
        Message = "Nao foi possivel acordar o despachante. O evento sai na proxima varredura.")]
    private static partial void RegistrarFalhaAoAcordar(ILogger logger, Exception excecao);
}
