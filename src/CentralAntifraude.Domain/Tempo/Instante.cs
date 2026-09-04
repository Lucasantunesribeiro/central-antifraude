namespace CentralAntifraude.Domain.Tempo;

/// <summary>
/// Normaliza um instante para a forma que o sistema inteiro usa: UTC, com a
/// mesma precisao que o banco consegue guardar.
///
/// **Por que truncar.** O <c>DateTimeOffset</c> do .NET conta em ticks de 100
/// nanossegundos; o <c>timestamptz</c> do PostgreSQL guarda microssegundos.
/// Gravar um instante com os digitos extras e le-lo de volta devolve um valor
/// DIFERENTE do que estava em memoria — a diferenca fica nas casas que o banco
/// descartou.
///
/// Isso apareceu na Fase 3 de um jeito concreto: a resposta da ingestao
/// carrega <c>avaliadaEm</c>. Na primeira requisicao o valor vinha da memoria;
/// no retry, do banco. O mesmo pedido devolvia dois horarios que diferiam em
/// nanossegundos, e o integrador nao teria como saber qual era o verdadeiro.
///
/// Truncar na origem elimina a classe inteira do problema: o que entra na
/// memoria e exatamente o que o banco vai devolver. Truncar, e nao arredondar,
/// porque e o que o driver faz na gravacao — arredondar aqui criaria a mesma
/// divergencia ao contrario.
/// </summary>
public static class Instante
{
    /// <summary>Ticks de 100 ns por microssegundo — a resolucao do PostgreSQL.</summary>
    public const long TicksPorMicrossegundo = 10;

    /// <summary>Converte para UTC e descarta o que o banco nao guardaria.</summary>
    public static DateTimeOffset Normalizar(DateTimeOffset instante)
    {
        var emUtc = instante.ToUniversalTime();

        return emUtc.AddTicks(-(emUtc.Ticks % TicksPorMicrossegundo));
    }
}
