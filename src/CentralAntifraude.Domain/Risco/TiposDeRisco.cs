namespace CentralAntifraude.Domain.Risco;

/// <summary>
/// Recomendacao de risco produzida pela avaliacao.
///
/// **Sao recomendacoes, nao resultado financeiro** (CLAUDE.md secao 10). A
/// Central Antifraude nao autoriza, nao captura e nao liquida — ela diz o que
/// o risco sugere, e o sistema de pagamento decide o que fazer com isso.
///
/// A tripla espelha a pratica descrita pelo U.S. Payments Forum para fraud
/// scoring: "A fraud score indicates whether an order should be rejected,
/// accepted, or further reviewed."
/// </summary>
public enum Decisao
{
    /// <summary>Risco baixo. Segue o fluxo normal.</summary>
    Permitir = 1,

    /// <summary>Risco intermediario. Vale olho humano antes de concluir.</summary>
    Revisar = 2,

    /// <summary>Risco alto. A recomendacao e nao prosseguir.</summary>
    Bloquear = 3,
}

/// <summary>
/// Catalogo FECHADO de tipos de regra.
///
/// CLAUDE.md secao 21: nao ha DSL propria, SQL configuravel nem script de
/// usuario. Cada tipo aqui tem um contrato de configuracao tipado e um
/// avaliador em C#. Configurar uma regra e escolher numeros dentro de um
/// contrato conhecido — nunca escrever logica que o sistema executa.
///
/// A diferenca importa: um JSON de configuracao com "janelaEmMinutos": 15 e
/// dado. Um JSON com uma expressao a ser interpretada seria codigo vindo do
/// banco, e e exatamente isso que o projeto recusa.
/// </summary>
public enum TipoDeRegra
{
    /// <summary>Muitas tentativas do mesmo cliente em uma janela curta.</summary>
    VelocidadePorCliente = 1,

    /// <summary>Dispositivo nunca visto antes para aquele cliente.</summary>
    NovoDispositivo = 2,

    /// <summary>Valor muito acima do que aquele cliente costuma gastar.</summary>
    ValorAcimaDoHistorico = 3,

    /// <summary>Pais de origem diferente do que aquele cliente costuma usar.</summary>
    DivergenciaGeografica = 4,
}
