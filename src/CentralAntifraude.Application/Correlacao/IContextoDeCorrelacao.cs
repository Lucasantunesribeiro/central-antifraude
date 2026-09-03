namespace CentralAntifraude.Application.Correlacao;

/// <summary>
/// Identificador que amarra tudo o que uma unica operacao externa produziu.
///
/// CLAUDE.md secao 69 exige que ele atravesse
/// HTTP -> dominio -> outbox -> mensagem -> worker -> efeito.
/// O contrato nasce na Fase 0 para que nenhuma camada criada depois precise
/// inventar a sua propria forma de propagar isso.
/// </summary>
public interface IContextoDeCorrelacao
{
    /// <summary>
    /// Identificador da operacao atual. Nunca nulo nem vazio: quando o
    /// cliente nao envia um valor aceitavel, a borda gera um.
    /// </summary>
    string IdDeCorrelacao { get; }
}
