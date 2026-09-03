using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// Protege a decisao de identificadores congelada na Fase 0
/// (docs/adr/0002-identificadores-internos.md).
/// </summary>
public sealed class IdentificadorTests
{
    [Fact]
    public void Novo_gera_uuid_da_versao_7()
    {
        var identificador = Identificador.Novo();

        Assert.Equal(7, identificador.Version);
        Assert.True(Identificador.EhDoFormatoAdotado(identificador));
    }

    [Fact]
    public void Novo_nunca_repete()
    {
        var gerados = Enumerable.Range(0, 1_000).Select(_ => Identificador.Novo()).ToList();

        Assert.Equal(gerados.Count, gerados.Distinct().Count());
    }

    [Fact]
    public void Identificadores_de_instantes_crescentes_crescem_byte_a_byte()
    {
        // A razao de existir do UUIDv7: o prefixo de tempo mantem as chaves
        // proximas no indice B-tree. Quem ordena de fato e o PostgreSQL, e o
        // tipo `uuid` dele compara byte a byte em big-endian - nao com o
        // Guid.CompareTo do .NET, que compara campo a campo com sinal.
        // Entao o teste compara do jeito que o banco compara.
        var origem = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var primeiro = Identificador.Novo(origem);
        var segundo = Identificador.Novo(origem.AddSeconds(1));
        var terceiro = Identificador.Novo(origem.AddMinutes(1));

        Assert.True(ComparadoComoNoPostgres(primeiro, segundo) < 0);
        Assert.True(ComparadoComoNoPostgres(segundo, terceiro) < 0);
    }

    private static int ComparadoComoNoPostgres(Guid esquerda, Guid direita) =>
        esquerda.ToByteArray(bigEndian: true)
            .AsSpan()
            .SequenceCompareTo(direita.ToByteArray(bigEndian: true));

    [Fact]
    public void Guid_vazio_nao_e_identificador_valido()
    {
        Assert.False(Identificador.EhDoFormatoAdotado(Guid.Empty));
    }

    [Fact]
    public void Uuid_da_versao_4_nao_e_do_formato_adotado()
    {
        // Guid.NewGuid() produz versao 4 e esta proibido em src/ pelo
        // analisador. Aqui o valor e escrito na mao so para provar a distincao.
        var versao4 = new Guid("6f9619ff-8b86-4d01-b42d-00cf4fc964ff");

        Assert.Equal(4, versao4.Version);
        Assert.False(Identificador.EhDoFormatoAdotado(versao4));
    }
}
