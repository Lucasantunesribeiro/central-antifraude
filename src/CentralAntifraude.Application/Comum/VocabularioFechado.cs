namespace CentralAntifraude.Application.Comum;

/// <summary>
/// Traducao de texto vindo do cliente para um enum fechado do dominio.
///
/// **Comparar com os NOMES, e nao usar <c>Enum.TryParse</c> como porta de
/// entrada.** O `TryParse` tambem aceita o numero subjacente, entao
/// <c>?decisao=2</c> viraria <c>Revisar</c> e <c>?prioridade=99</c>
/// atravessaria como um valor que nao existe no enum. Comparar com a lista de
/// nomes antes de converter fecha os dois buracos e deixa o vocabulario aceito
/// explicito na mensagem de erro.
///
/// **Recusar, nunca ignorar.** Um filtro desconhecido descartado em silencio
/// devolveria a lista inteira, e quem consultou acreditaria estar vendo so o
/// que pediu. Em telas de fraude, acreditar que se filtrou e pior do que
/// receber um erro.
/// </summary>
public static class VocabularioFechado
{
    public static bool TentarResolver<T>(string? texto, string nome, out T? valor, out string erro)
        where T : struct, Enum
    {
        valor = null;
        erro = string.Empty;

        if (string.IsNullOrWhiteSpace(texto))
        {
            return true;
        }

        var informado = texto.Trim();

        var canonico = Enum.GetNames<T>().FirstOrDefault(
            aceito => string.Equals(aceito, informado, StringComparison.OrdinalIgnoreCase));

        if (canonico is not null)
        {
            valor = Enum.Parse<T>(canonico);
            return true;
        }

        // Os nomes aceitos vao na mensagem de proposito: sao contrato publico,
        // e nao ha o que vazar em dizer quais valores existem.
        erro = $"Valor invalido para '{nome}'. Aceitos: {string.Join(", ", Enum.GetNames<T>())}.";

        return false;
    }
}
