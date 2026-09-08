namespace CentralAntifraude.Application.Correlacao;

/// <summary>
/// O portador do identificador de correlacao dentro de um escopo.
///
/// **Por que ele saiu da Api na Fase 12.** Ate aqui o unico lugar que definia
/// correlacao era o middleware HTTP, e a classe morava junto dele. So que o fio
/// precisa continuar do outro lado da fila: quando o worker processa um evento,
/// a correlacao existe — ela veio no envelope — mas nada a colocava de volta em
/// circulacao, e todo log e todo efeito produzido pelo worker nascia sem ela.
/// Uma investigacao que seguisse um `CorrelationId` chegava ate a publicacao e
/// perdia o rastro exatamente onde ele fica mais dificil de reconstruir a mao.
///
/// **Definir e escrita, ler e contrato.** Quem consome depende de
/// <see cref="IContextoDeCorrelacao"/>, que so le. Somente as duas bordas que
/// sabem de onde vem a correlacao — o middleware HTTP e o consumidor de
/// eventos — dependem desta classe concreta.
///
/// Vive com escopo. Em uma requisicao, o escopo e a requisicao; em um laco de
/// fundo, e o ciclo. Um singleton aqui embaralharia operacoes simultaneas,
/// atribuindo a correlacao de uma requisicao aos efeitos de outra.
/// </summary>
public sealed class ContextoDeCorrelacaoMutavel : IContextoDeCorrelacao
{
    /// <summary>Tamanho maximo aceito, igual ao da coluna da Outbox.</summary>
    public const int TamanhoMaximo = 64;

    public string IdDeCorrelacao { get; private set; } = string.Empty;

    /// <summary>
    /// Define a correlacao do escopo atual.
    ///
    /// Recorta no tamanho maximo em vez de recusar: quem chama daqui e uma
    /// borda de entrada, e derrubar o processamento de um evento por causa do
    /// comprimento de um identificador de diagnostico seria trocar um problema
    /// de log por uma mensagem na fila de mortas.
    /// </summary>
    public void Definir(string idDeCorrelacao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idDeCorrelacao);

        var limpo = idDeCorrelacao.Trim();

        IdDeCorrelacao = limpo.Length > TamanhoMaximo
            ? limpo[..TamanhoMaximo]
            : limpo;
    }
}
