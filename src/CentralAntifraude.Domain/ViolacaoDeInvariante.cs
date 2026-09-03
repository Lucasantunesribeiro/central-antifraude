namespace CentralAntifraude.Domain;

/// <summary>
/// Uma regra que o dominio garante sempre foi quebrada.
///
/// Isto NAO e erro de entrada do usuario: entrada invalida e barrada na borda
/// e vira resposta HTTP 400. Esta excecao significa que um objeto de dominio
/// foi construido em estado impossivel - ou seja, defeito de programacao.
/// </summary>
public sealed class ViolacaoDeInvariante : Exception
{
    public ViolacaoDeInvariante(string mensagem)
        : base(mensagem)
    {
    }

    public ViolacaoDeInvariante(string mensagem, Exception causa)
        : base(mensagem, causa)
    {
    }
}
