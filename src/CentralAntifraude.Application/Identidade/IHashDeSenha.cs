namespace CentralAntifraude.Application.Identidade;

public enum ResultadoDaVerificacaoDeSenha
{
    /// <summary>Senha nao confere.</summary>
    Invalida,

    /// <summary>Senha confere.</summary>
    Valida,

    /// <summary>
    /// Senha confere, mas o hash foi gerado com parametros antigos.
    /// Quem chamou deve regravar o hash — e a unica oportunidade de fazer
    /// isso, porque so aqui a senha em claro esta disponivel.
    /// </summary>
    ValidaMasPrecisaRegravar,
}

/// <summary>
/// Derivacao e verificacao de senha.
///
/// Contrato no Application e implementacao na Infrastructure porque o
/// algoritmo e um detalhe que vai mudar com o tempo — o dominio nao deve
/// saber qual e.
/// </summary>
public interface IHashDeSenha
{
    string Gerar(string senha);

    ResultadoDaVerificacaoDeSenha Verificar(string hashArmazenado, string senhaInformada);
}
