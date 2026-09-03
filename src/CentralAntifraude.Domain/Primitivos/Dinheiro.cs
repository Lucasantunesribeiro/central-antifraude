using System.Globalization;

namespace CentralAntifraude.Domain.Primitivos;

/// <summary>
/// Valor monetario com moeda explicita.
///
/// Decisao congelada na Fase 0 (CLAUDE.md secao 101): dinheiro usa
/// <see cref="decimal"/> - nunca <c>float</c>/<c>double</c> - e a moeda
/// sempre viaja junto do valor, porque o produto aceita mais de uma.
///
/// Esta struct NAO decide se um valor negativo faz sentido: isso e invariante
/// do contrato que a usa (uma tentativa de pagamento exige valor positivo;
/// um ajuste futuro pode nao exigir). Ela garante apenas o que vale sempre:
/// moeda no formato ISO 4217 alfabetico e escala compativel com persistencia.
///
/// Atencao: <c>default(Dinheiro)</c> nao e um valor valido - use
/// <see cref="EhValido"/> quando a origem do dado nao for confiavel.
/// </summary>
public readonly record struct Dinheiro
{
    /// <summary>Casas decimais maximas aceitas, alinhadas a coluna numeric(18,4).</summary>
    public const int EscalaMaxima = 4;

    private readonly string? _moeda;

    private Dinheiro(decimal valor, string moeda)
    {
        Valor = valor;
        _moeda = moeda;
    }

    /// <summary>Quantia, sempre em <see cref="decimal"/>.</summary>
    public decimal Valor { get; }

    /// <summary>Codigo ISO 4217 alfabetico, em maiusculas. Ex.: "BRL", "USD".</summary>
    public string Moeda => _moeda ?? string.Empty;

    /// <summary>Falso para <c>default(Dinheiro)</c>, que nunca passou por validacao.</summary>
    public bool EhValido => _moeda is not null;

    /// <summary>
    /// Cria um valor monetario validado.
    /// </summary>
    /// <exception cref="ViolacaoDeInvariante">Se moeda ou escala forem invalidas.</exception>
    public static Dinheiro De(decimal valor, string moeda)
    {
        if (!TentarCriar(valor, moeda, out var dinheiro, out var erro))
        {
            throw new ViolacaoDeInvariante(erro);
        }

        return dinheiro;
    }

    /// <summary>
    /// Tentativa de criacao sem excecao, para uso na borda de validacao
    /// (onde o erro precisa virar resposta HTTP, nao exception).
    /// </summary>
    public static bool TentarCriar(
        decimal valor,
        string? moeda,
        out Dinheiro dinheiro,
        out string erro)
    {
        dinheiro = default;

        if (string.IsNullOrWhiteSpace(moeda))
        {
            erro = "Moeda e obrigatoria.";
            return false;
        }

        var normalizada = moeda.Trim().ToUpperInvariant();

        if (normalizada.Length != 3 || !normalizada.All(c => c is >= 'A' and <= 'Z'))
        {
            erro = "Moeda deve ser um codigo ISO 4217 alfabetico de 3 letras.";
            return false;
        }

        if (EscalaDe(valor) > EscalaMaxima)
        {
            erro = $"Valor monetario aceita no maximo {EscalaMaxima} casas decimais.";
            return false;
        }

        dinheiro = new Dinheiro(valor, normalizada);
        erro = string.Empty;
        return true;
    }

    /// <summary>Numero de casas decimais efetivamente presentes no decimal.</summary>
    private static int EscalaDe(decimal valor) => (decimal.GetBits(valor)[3] >> 16) & 0xFF;

    public override string ToString() =>
        EhValido
            ? string.Create(CultureInfo.InvariantCulture, $"{Valor} {Moeda}")
            : "<dinheiro nao inicializado>";
}
