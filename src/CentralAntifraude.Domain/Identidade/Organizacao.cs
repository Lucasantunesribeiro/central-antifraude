using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Identidade;

/// <summary>
/// Uma organizacao cliente da Central Antifraude — o tenant.
///
/// Tudo o que o produto cria pertence a exatamente uma organizacao:
/// usuarios, integracoes, transacoes, regras, alertas, casos e auditoria
/// (CLAUDE.md secao 9).
///
/// A organizacao e o unico agregado que NAO carrega TenantId, porque ela
/// propria e o tenant.
/// </summary>
public sealed class Organizacao
{
    private Organizacao(Guid id, string nome, string codigo, DateTimeOffset criadaEm)
    {
        Id = id;
        Nome = nome;
        Codigo = codigo;
        Ativa = true;
        CriadaEm = criadaEm;
    }

    // Construtor usado pelo EF Core na materializacao.
    private Organizacao()
    {
        Nome = string.Empty;
        Codigo = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Nome de exibicao.</summary>
    public string Nome { get; private set; }

    /// <summary>
    /// Codigo curto, estavel e legivel, em minusculas.
    /// Usado em log e suporte, onde citar um UUID inteiro e impraticavel.
    /// </summary>
    public string Codigo { get; private set; }

    /// <summary>
    /// Organizacao inativa nao autentica ninguem. Desativar e a operacao
    /// reversivel; apagar dados de fraude nao seria.
    /// </summary>
    public bool Ativa { get; private set; }

    public DateTimeOffset CriadaEm { get; private set; }

    public const int TamanhoMaximoDoNome = 200;
    public const int TamanhoMaximoDoCodigo = 60;

    public static Organizacao Criar(string nome, string codigo, DateTimeOffset agora)
    {
        if (string.IsNullOrWhiteSpace(nome) || nome.Trim().Length > TamanhoMaximoDoNome)
        {
            throw new ViolacaoDeInvariante(
                $"Nome da organizacao e obrigatorio e deve ter no maximo {TamanhoMaximoDoNome} caracteres.");
        }

        return new Organizacao(
            Identificador.Novo(),
            nome.Trim(),
            NormalizarCodigo(codigo),
            agora);
    }

    public void Desativar() => Ativa = false;

    public void Reativar() => Ativa = true;

    private static string NormalizarCodigo(string codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo))
        {
            throw new ViolacaoDeInvariante("Codigo da organizacao e obrigatorio.");
        }

        var normalizado = codigo.Trim().ToLowerInvariant();

        if (normalizado.Length > TamanhoMaximoDoCodigo)
        {
            throw new ViolacaoDeInvariante(
                $"Codigo deve ter no maximo {TamanhoMaximoDoCodigo} caracteres.");
        }

        // Conjunto fechado: o codigo aparece em log e em URL de suporte.
        var permitido = normalizado.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

        if (!permitido || normalizado.StartsWith('-') || normalizado.EndsWith('-'))
        {
            throw new ViolacaoDeInvariante(
                "Codigo aceita apenas letras minusculas, digitos e hifen interno.");
        }

        return normalizado;
    }
}
