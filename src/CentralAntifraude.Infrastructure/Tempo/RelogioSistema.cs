using CentralAntifraude.Domain.Tempo;

namespace CentralAntifraude.Infrastructure.Tempo;

/// <summary>
/// Unico ponto do sistema autorizado a ler o relogio real.
///
/// Todo o resto do codigo recebe <see cref="IRelogio"/> por injecao, o que
/// torna testavel qualquer regra que dependa de janela de tempo.
/// </summary>
public sealed class RelogioSistema : IRelogio
{
    // Suprimido de proposito: esta e a implementacao que o BannedSymbols.txt
    // existe para tornar unica. Se aparecer um segundo #pragma como este em
    // src/, a decisao foi burlada.
    //
    // O valor passa por Instante.Normalizar: o relogio so produz instantes
    // que o PostgreSQL consegue guardar sem perder digito. Sem isso, o mesmo
    // horario lido da memoria e lido do banco seria diferente nas casas de
    // nanossegundo.
#pragma warning disable RS0030
    public DateTimeOffset Agora => Instante.Normalizar(DateTimeOffset.UtcNow);
#pragma warning restore RS0030
}
