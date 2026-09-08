using System.Diagnostics.CodeAnalysis;
using Amazon.Lambda.Core;
using CentralAntifraude.Infrastructure.Mensageria;
using CentralAntifraude.Infrastructure.Observabilidade;
using Microsoft.Extensions.DependencyInjection;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace CentralAntifraude.Lambdas;

/// <summary>
/// Publica o que está pendente na Outbox.
///
/// **Duas coisas acionam esta função, e elas têm papéis diferentes.**
///
/// 1. A API, logo depois de confirmar uma transação. É o caminho normal, e é
///    o que faz um alerta existir em segundos.
/// 2. Um agendamento a cada quinze minutos. É a rede de segurança — o aviso
///    da API acontece depois do commit e pode falhar.
///
/// Sem o item 2, uma invocação perdida significaria um evento parado para
/// sempre. Sem o item 1, todo alerta esperaria até quinze minutos. A garantia
/// vem do segundo; a experiência, do primeiro.
///
/// **O laço interno esvazia a Outbox, e não publica um lote só.** Uma
/// varredura que saísse com vinte eventos publicados e trezentos pendentes
/// deixaria o resto esperando os próximos quinze minutos. O teto de ciclos
/// existe para que uma Outbox represada não estoure o tempo da função — o que
/// sobrar sai na varredura seguinte, que é exatamente o comportamento correto.
/// </summary>
public sealed class DespachanteHandler
{
    /// <summary>
    /// Quantos lotes por invocação. Vinte eventos por lote, então até 600 por
    /// execução — mais do que o envelope de portfólio produz num mês.
    /// </summary>
    private const int MaximoDeCiclos = 30;

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
    public async Task<string> TratarAsync(object? evento, ILambdaContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);

        var publicados = 0;
        var falhados = 0;

        for (var ciclo = 0; ciclo < MaximoDeCiclos; ciclo++)
        {
            // Um escopo por ciclo, como o laço em processo fazia. Sem isso o
            // DbContext acumularia as entidades de todos os lotes até o fim da
            // invocação, e a memória cresceria com o tamanho do represamento.
            using var escopo = Hospedagem.AbrirEscopo();

            var resultado = await escopo.ServiceProvider
                .GetRequiredService<DespachanteDeEventos>()
                .DespacharLoteAsync(CancellationToken.None);

            publicados += resultado.Publicados;
            falhados += resultado.Falhados;

            if (resultado.Total == 0)
            {
                break;
            }

            // Sobrou tempo? Se a função está perto do limite, para agora e
            // deixa o resto para a próxima. Ser interrompido pelo Lambda no
            // meio de um lote é seguro — o evento continua pendente —, mas
            // custa a invocação inteira sem publicar nada do lote em curso.
            if (contexto.RemainingTime < TimeSpan.FromSeconds(10))
            {
                break;
            }
        }

        // A amostragem de profundidade pega carona aqui, como pegava no laço
        // em processo: é o único ponto do sistema que já tem conexão aberta e
        // roda no ritmo certo.
        using (var escopo = Hospedagem.AbrirEscopo())
        {
            await escopo.ServiceProvider
                .GetRequiredService<AmostradorDeIndicadores>()
                .AmostrarSeVencidoAsync(CancellationToken.None);
        }

        return $"publicados={publicados} falhados={falhados}";
    }
}
