namespace CentralAntifraude.Application.Erros;

/// <summary>
/// Uma restricao unica do banco foi violada.
///
/// **Nao e um erro de aplicacao** — nao herda de <see cref="ErroDeAplicacao"/>
/// e nao vira resposta HTTP por conta propria. E um sinal interno: quem
/// chamou sabe o que fazer com ele.
///
/// No caso da ingestao, significa que outra requisicao com a mesma chave de
/// idempotencia venceu a corrida entre a consulta e o INSERT. A resposta
/// correta nao e um erro — e devolver o resultado da vencedora.
///
/// A traducao do codigo do PostgreSQL (<c>23505</c>) para esta excecao
/// acontece na Infrastructure, para que a camada de aplicacao continue sem
/// conhecer EF Core nem Npgsql.
/// </summary>
public sealed class ConflitoDeUnicidadeNoBanco : Exception
{
    public ConflitoDeUnicidadeNoBanco(string? restricao, Exception causa)
        : base(
            restricao is null
                ? "Uma restricao de unicidade foi violada."
                : $"A restricao de unicidade '{restricao}' foi violada.",
            causa)
    {
        Restricao = restricao;
    }

    /// <summary>
    /// Nome da restricao violada, quando o provedor informa. Serve para
    /// distinguir qual invariante foi tocada sem ter que adivinhar.
    /// </summary>
    public string? Restricao { get; }
}
