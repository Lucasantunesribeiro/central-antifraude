using CentralAntifraude.Application.Correlacao;

namespace CentralAntifraude.Api.Correlacao;

/// <summary>
/// Implementacao por requisicao do identificador de correlacao.
/// Preenchida uma unica vez pelo <see cref="MiddlewareDeCorrelacao"/>.
/// </summary>
public sealed class ContextoDeCorrelacao : IContextoDeCorrelacao
{
    public string IdDeCorrelacao { get; private set; } = string.Empty;

    internal void Definir(string idDeCorrelacao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idDeCorrelacao);
        IdDeCorrelacao = idDeCorrelacao;
    }
}
