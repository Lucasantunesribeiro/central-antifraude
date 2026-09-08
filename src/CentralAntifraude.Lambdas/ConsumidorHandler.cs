using System.Diagnostics.CodeAnalysis;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Infrastructure.Mensageria;
using Microsoft.Extensions.DependencyInjection;

namespace CentralAntifraude.Lambdas;

/// <summary>
/// Aplica os efeitos operacionais de cada evento entregue pela fila.
///
/// **O consumidor não foi reescrito.** `ProcessadorDeEventos`, com a Inbox, o
/// savepoint por efeito e a conferência de tenant, é exatamente o mesmo da
/// Fase 5 — o que muda é de onde as mensagens vêm e quem as confirma. A ponte
/// é a <see cref="FilaDoEventoLambda"/>: ela finge ser uma fila, entrega o que
/// veio no evento e converte "devolver" em falha reportada.
///
/// Reescrever o consumidor para o modelo do Lambda teria significado reprovar,
/// em código novo, tudo o que dez fases de teste garantem sobre ele.
///
/// **Falha parcial por item.** Sem `batchItemFailures`, uma mensagem ruim no
/// meio de nove boas faria a AWS reentregar as dez. A Inbox tornaria isso
/// inofensivo — os efeitos não aconteceriam de novo — mas inofensivo não é de
/// graça: são nove leituras de banco e nove transações para descobrir que não
/// havia nada a fazer.
/// </summary>
public sealed class ConsumidorHandler
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

        using var escopo = Hospedagem.AbrirEscopo();

        // A fila real serve para ENVIAR (um efeito pode publicar) e para
        // contar; o recebimento vem do evento.
        var envio = escopo.ServiceProvider.GetRequiredService<IFilaDeMensagens>();

        var fila = new FilaDoEventoLambda(envio, LoteDoSqs.Ler(evento));

        // Tudo vem do contêiner, menos a fila, que é o adaptador desta
        // invocação. Listar as outras dependências à mão faria este arquivo
        // precisar de manutenção toda vez que o consumidor ganhasse uma —
        // e o esquecimento apareceria só em produção.
        var processador = ActivatorUtilities.CreateInstance<ProcessadorDeEventos>(
            escopo.ServiceProvider,
            fila);

        await processador.ConsumirLoteAsync(CancellationToken.None);

        // Falha é ausência de confirmação, e não uma lista que o consumidor
        // devolve. Quem faz essa conta é a FilaDoEventoLambda, por subtração:
        // entregue e não confirmado significa "não terminou bem", inclusive
        // para o desfecho em que o consumidor deixaria a visibilidade expirar.
        return new SQSBatchResponse
        {
            BatchItemFailures = [.. fila.RecibosComFalha.Select(
                id => new SQSBatchResponse.BatchItemFailure { ItemIdentifier = id })],
        };
    }


}
