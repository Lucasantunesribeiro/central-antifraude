using System.Diagnostics.CodeAnalysis;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Infrastructure.Mensageria;
using Microsoft.Extensions.DependencyInjection;

namespace CentralAntifraude.Lambdas;

/// <summary>
/// Executa os backtests pedidos, na fila separada deles.
///
/// **Por que uma função só para isto.** Um backtest reprocessa histórico e leva
/// dezenas de segundos; um evento operacional leva milissegundos. Compartilhar
/// a fila faria um backtest de meia hora atrasar todos os alertas atrás dele —
/// foi a razão de separar as filas na Fase 9, e a separação continua valendo
/// aqui, agora como duas funções com tempo limite e concorrência próprios.
///
/// **Uma mensagem por invocação.** O gatilho está configurado com `BatchSize: 1`
/// no template, e o laço abaixo existe mesmo assim: ele torna o handler correto
/// para qualquer tamanho de lote em vez de correto por coincidência de
/// configuração. Se alguém aumentar o lote no template, nada aqui se perde.
/// </summary>
public sealed class BacktestHandler
{
    /// <summary>
    /// Não é estático de propósito, e a regra CA1822 pediria que fosse.
    ///
    /// A AWS documenta o handler de biblioteca de classes como um método que
    /// ela alcança instanciando a classe — "your function's class is
    /// initialized, and any code in the constructor is run". Método estático
    /// não aparece como suportado em lugar nenhum da página de handlers em C#,
    /// e uma economia de uma alocação por arranque frio não vale apostar o
    /// deploy inteiro numa suposição que a documentação não confirma.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "O handler de biblioteca de classes do Lambda e alcancado por instancia.")]
    public async Task<SQSBatchResponse> TratarAsync(SQSEvent evento, ILambdaContext contexto)
    {
        ArgumentNullException.ThrowIfNull(evento);
        ArgumentNullException.ThrowIfNull(contexto);

        using var escopo = Hospedagem.AbrirEscopo();

        var envio = escopo.ServiceProvider.GetRequiredService<IFilaDeMensagens>();

        var fila = new FilaDoEventoLambda(envio, LoteDoSqs.Ler(evento));

        var processador = ActivatorUtilities.CreateInstance<ProcessadorDeBacktests>(
            escopo.ServiceProvider,
            fila);

        while (true)
        {
            var resultado = await processador.ConsumirLoteAsync(CancellationToken.None);

            if (resultado.Total == 0)
            {
                break;
            }

            // Um backtest inteiro não cabe no tempo que sobrou? Para agora. A
            // mensagem seguinte não foi entregue ao processador, então ela
            // continua na fila e volta na próxima invocação — parar aqui é
            // mais barato do que ser interrompido no meio de uma apuração.
            if (contexto.RemainingTime < TimeSpan.FromMinutes(1))
            {
                break;
            }
        }

        return new SQSBatchResponse
        {
            BatchItemFailures = [.. fila.RecibosComFalha.Select(
                id => new SQSBatchResponse.BatchItemFailure { ItemIdentifier = id })],
        };
    }


}
