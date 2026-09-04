using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CentralAntifraude.Application.Transacoes;

/// <summary>
/// Conteudo de uma tentativa de pagamento, como a integracao a envia.
///
/// Contrato fechado (CLAUDE.md secoes 53 e 54): estes campos e mais nenhum.
/// Nao ha saco de metadados livres — um dicionario aberto viraria, com o
/// tempo, o lugar onde alguem coloca CPF, nome e numero de cartao "so por
/// enquanto". Quando um campo novo for necessario, ele entra aqui, com nome,
/// tipo e validacao.
///
/// Note o que NAO existe: numero de cartao, CVV, titular, conta bancaria.
/// Nao e uma regra que alguem precisa lembrar de seguir — nao ha onde
/// escrever esses dados.
/// </summary>
public sealed record ConteudoDaTransacao(
    string? IdentificadorExterno,
    decimal Valor,
    string? Moeda,
    DateTimeOffset OcorridaEm,
    string? ClienteExternoId,
    string? ReferenciaDoInstrumento,
    string? FingerprintDoDispositivo,
    string? EnderecoIp,
    string? PaisDeOrigem);

/// <summary>
/// Hash canonico do conteudo de uma requisicao de ingestao.
///
/// **O problema que resolve.** A idempotencia precisa distinguir "mesma chave,
/// mesmo pedido" de "mesma chave, pedido diferente". Comparar o corpo bruto
/// nao serve: dois JSON com os mesmos dados em ordem diferente, ou com um
/// espaco a mais, sao o mesmo pedido — e seriam vistos como conflito.
///
/// **Como funciona.** O hash e calculado sobre o conteudo JA desserializado e
/// normalizado, em ordem fixa de campos, com cada valor escrito exatamente
/// como sera persistido:
///
/// <list type="bullet">
///   <item>datas em UTC, com precisao de milissegundo;</item>
///   <item>decimal com escala fixa, entao <c>10</c>, <c>10.0</c> e
///         <c>10.0000</c> produzem o mesmo hash — porque produzem a mesma
///         linha no banco;</item>
///   <item>textos aparados; moeda e pais em maiusculas;</item>
///   <item>ausente e vazio sao a mesma coisa.</item>
/// </list>
///
/// Cada campo e prefixado pelo proprio nome e separado por um caractere que
/// nao aparece em nenhum valor valido. Sem isso, mover um caractere de um
/// campo para o vizinho produziria o mesmo hash — a ambiguidade classica de
/// concatenacao.
/// </summary>
public static class FingerprintDaRequisicao
{
    /// <summary>Separador de unidade (ASCII 31). Nao ocorre em texto valido.</summary>
    private const char Separador = '';

    /// <summary>Casas decimais usadas na forma canonica, iguais as da coluna.</summary>
    private const int EscalaCanonica = 4;

    public static string Calcular(ConteudoDaTransacao conteudo)
    {
        ArgumentNullException.ThrowIfNull(conteudo);

        var canonico = new StringBuilder(256);

        Acrescentar(canonico, "identificadorExterno", Normalizar(conteudo.IdentificadorExterno));
        Acrescentar(canonico, "valor", conteudo.Valor.ToString($"F{EscalaCanonica}", CultureInfo.InvariantCulture));
        Acrescentar(canonico, "moeda", Normalizar(conteudo.Moeda)?.ToUpperInvariant());
        Acrescentar(canonico, "ocorridaEm", conteudo.OcorridaEm.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Acrescentar(canonico, "clienteExternoId", Normalizar(conteudo.ClienteExternoId));
        Acrescentar(canonico, "referenciaDoInstrumento", Normalizar(conteudo.ReferenciaDoInstrumento));
        Acrescentar(canonico, "fingerprintDoDispositivo", Normalizar(conteudo.FingerprintDoDispositivo));
        Acrescentar(canonico, "enderecoIp", Normalizar(conteudo.EnderecoIp));
        Acrescentar(canonico, "paisDeOrigem", Normalizar(conteudo.PaisDeOrigem)?.ToUpperInvariant());

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonico.ToString()));

        return Convert.ToHexStringLower(bytes);
    }

    private static void Acrescentar(StringBuilder destino, string campo, string? valor)
    {
        destino.Append(campo).Append('=').Append(valor ?? string.Empty).Append(Separador);
    }

    private static string? Normalizar(string? valor) =>
        string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
}
