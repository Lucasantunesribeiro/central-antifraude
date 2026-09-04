namespace CentralAntifraude.Domain.Primitivos;

/// <summary>
/// Endereco de e-mail normalizado.
///
/// Existe por um motivo concreto: o e-mail e a chave unica global de usuario
/// (ver docs/adr/0005). Se "Ana@Empresa.com" e "ana@empresa.com" puderem
/// coexistir, a restricao unica do banco nao protege nada e dois usuarios
/// diferentes disputam a mesma identidade.
///
/// Normalizar em um lugar so - aqui - torna esse erro impossivel de cometer
/// por distracao em uma consulta.
/// </summary>
public readonly record struct Email
{
    /// <summary>Limite alinhado a coluna e a pratica comum de provedores.</summary>
    public const int TamanhoMaximo = 254;

    private readonly string? _valor;

    private Email(string valor) => _valor = valor;

    public string Valor => _valor ?? string.Empty;

    public bool EhValido => _valor is not null;

    /// <exception cref="ViolacaoDeInvariante">Se o endereco for invalido.</exception>
    public static Email De(string valor)
    {
        if (!TentarCriar(valor, out var email, out var erro))
        {
            throw new ViolacaoDeInvariante(erro);
        }

        return email;
    }

    /// <summary>
    /// Validacao deliberadamente conservadora: um unico "@", com conteudo dos
    /// dois lados, um ponto no dominio e sem espaco.
    ///
    /// Nao tenta implementar a RFC 5322 - a gramatica completa aceita coisas
    /// que nenhum provedor entrega, e um regex ambicioso aqui trocaria um
    /// problema real (digitacao errada) por outro pior (falso negativo em
    /// endereco valido, ou catastrophic backtracking).
    /// </summary>
    public static bool TentarCriar(string? valor, out Email email, out string erro)
    {
        email = default;

        if (string.IsNullOrWhiteSpace(valor))
        {
            erro = "E-mail e obrigatorio.";
            return false;
        }

        var normalizado = valor.Trim().ToLowerInvariant();

        if (normalizado.Length > TamanhoMaximo)
        {
            erro = $"E-mail deve ter no maximo {TamanhoMaximo} caracteres.";
            return false;
        }

        var partes = normalizado.Split('@');

        if (partes.Length != 2 ||
            partes[0].Length == 0 ||
            partes[1].Length == 0 ||
            !partes[1].Contains('.', StringComparison.Ordinal) ||
            partes[1].StartsWith('.') ||
            partes[1].EndsWith('.') ||
            normalizado.Any(char.IsWhiteSpace))
        {
            erro = "E-mail invalido.";
            return false;
        }

        email = new Email(normalizado);
        erro = string.Empty;
        return true;
    }

    public override string ToString() => Valor;
}
