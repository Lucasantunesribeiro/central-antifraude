namespace CentralAntifraude.Domain.Primitivos;

/// <summary>
/// Validacao compartilhada dos textos que uma pessoa escreve no produto.
///
/// **Recusar, nunca limpar em silencio.** Um titulo, uma nota ou o nome de
/// uma regra que o sistema altera sozinho deixa de ser o que a pessoa
/// escreveu — e numa investigacao ou numa configuracao de motor isso e pior
/// do que uma recusa com explicacao.
///
/// Vive em Primitivos, e nao em Investigacao, porque a Fase 8 passou a
/// escrever nome de regra pela mesma porta: a regra de texto e uma so.
/// </summary>
public static class TextoDeUsuario
{
    /// <summary>
    /// Valida sem lancar, para que cada camada escolha como reagir.
    ///
    /// A regra vive aqui, em um lugar so. O dominio a usa e lanca invariante;
    /// a camada de aplicacao a usa e devolve erro de validacao, que e o codigo
    /// HTTP correto para "voce escreveu algo invalido" — 409 diria que o
    /// recurso esta num estado incompativel, o que seria mentira.
    /// </summary>
    public static bool EhValido(
        string? texto,
        int tamanhoMinimo,
        int tamanhoMaximo,
        string campo,
        out string limpo,
        out string erro)
    {
        limpo = (texto ?? string.Empty).Trim();

        if (limpo.Length < tamanhoMinimo || limpo.Length > tamanhoMaximo)
        {
            erro = $"O campo '{campo}' deve ter entre {tamanhoMinimo} e {tamanhoMaximo} caracteres.";
            return false;
        }

        if (PareceMarcacao(limpo))
        {
            erro = $"O campo '{campo}' nao pode conter marcacao. " +
                   "Escreva em texto puro — o sistema nao altera o que voce escreveu.";
            return false;
        }

        if (TemControleProibido(limpo))
        {
            erro = $"O campo '{campo}' contem caracteres de controle nao permitidos.";
            return false;
        }

        erro = string.Empty;
        return true;
    }

    /// <summary>
    /// Parece uma tag de marcacao?
    ///
    /// A regra e estreita de proposito: `&lt;` seguido de letra ou de barra.
    /// Assim `valor &lt; 100` continua sendo uma nota valida, enquanto
    /// `&lt;script&gt;` e `&lt;/b&gt;` sao recusados. Recusar todo `&lt;`
    /// tornaria impossivel escrever uma comparacao numerica — que e
    /// exatamente o tipo de coisa que se escreve investigando fraude.
    /// </summary>
    public static bool PareceMarcacao(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);

        for (var i = 0; i < texto.Length - 1; i++)
        {
            if (texto[i] == '<' && (char.IsLetter(texto[i + 1]) || texto[i + 1] == '/'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Caracteres de controle que nao tem lugar em texto escrito por gente.
    ///
    /// Quebra de linha e tabulacao passam: uma nota de investigacao tem
    /// paragrafos. O resto — bytes nulos, escapes de terminal — nao vem de um
    /// teclado e nao deve entrar no banco.
    /// </summary>
    public static bool TemControleProibido(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);

        foreach (var caractere in texto)
        {
            if (char.IsControl(caractere) && caractere is not ('\n' or '\r' or '\t'))
            {
                return true;
            }
        }

        return false;
    }
}
