namespace CentralAntifraude.Domain.Primitivos;

/// <summary>
/// Fabrica unica de identificadores internos da Central Antifraude.
///
/// Decisao congelada na Fase 0 (docs/adr/0002-identificadores-internos.md):
/// todo identificador interno e um UUID versao 7 (RFC 9562), gerado pela
/// aplicacao e persistido na coluna PostgreSQL `uuid`.
///
/// Motivo: o UUIDv7 carrega um prefixo de tempo, entao chaves geradas em
/// sequencia caem proximas no indice B-tree - ao contrario do UUIDv4, que
/// espalha as insercoes e fragmenta o indice. E nao expoe contagem interna,
/// ao contrario de um `bigint` sequencial.
/// </summary>
public static class Identificador
{
    /// <summary>Gera um novo identificador interno com o instante atual.</summary>
#pragma warning disable RS0030 // Guid.CreateVersion7() e a unica fonte autorizada de identificador.
    public static Guid Novo() => Guid.CreateVersion7();
#pragma warning restore RS0030

    /// <summary>
    /// Gera um identificador ancorado em um instante especifico.
    /// Usado por testes deterministicos e por cenarios que precisam de
    /// ordenacao previsivel.
    /// </summary>
    public static Guid Novo(DateTimeOffset instante) => Guid.CreateVersion7(instante);

    /// <summary>
    /// Indica se o identificador segue a versao adotada pelo projeto.
    /// Serve de guarda em testes e em validacao de borda.
    /// </summary>
    public static bool EhDoFormatoAdotado(Guid identificador) =>
        identificador != Guid.Empty && identificador.Version == 7;
}
