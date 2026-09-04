using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Infrastructure.Persistencia.Configuracoes;

namespace CentralAntifraude.UnitTests.Infraestrutura;

/// <summary>
/// A configuracao de regra vai e volta do banco sem perder nada — e sem virar
/// um caminho para executar o que estiver gravado.
///
/// O CLAUDE.md secao 21 proibe DSL propria, SQL configuravel e script de
/// usuario. O que fica no banco e um objeto de dados com numeros e o nome de
/// um tipo conhecido. Estes testes travam as duas metades disso: o
/// round-trip precisa preservar os valores, e um tipo desconhecido precisa
/// falhar alto em vez de produzir comportamento.
/// </summary>
public class SerializadorDeConfiguracaoDeRegraTests
{
    private static readonly ConfiguracaoDeRegra[] Amostras =
    [
        new ConfiguracaoDeVelocidade(MaximoDeTransacoes: 3, JanelaEmMinutos: 10),
        new ConfiguracaoDeNovoDispositivo(MinimoDeTransacoesNoHistorico: 5),
        new ConfiguracaoDeValorAcimaDoHistorico(MultiploDaMedia: 4.5m, MinimoDeTransacoesNoHistorico: 7),
        new ConfiguracaoDeDivergenciaGeografica(MinimoDeTransacoesNoHistorico: 2),
    ];

    public static TheoryData<ConfiguracaoDeRegra> Configuracoes => [.. Amostras];

    [Theory]
    [MemberData(nameof(Configuracoes))]
    public void Round_trip_preserva_tipo_e_valores(ConfiguracaoDeRegra original)
    {
        var json = SerializadorDeConfiguracaoDeRegra.Serializar(original);
        var reconstruida = SerializadorDeConfiguracaoDeRegra.Desserializar(json);

        // Records comparam por valor: esta unica igualdade cobre todos os
        // campos, inclusive os que forem adicionados depois.
        Assert.Equal(original, reconstruida);
        Assert.Equal(original.Tipo, reconstruida.Tipo);
    }

    [Fact]
    public void Todo_tipo_do_catalogo_tem_configuracao_serializavel()
    {
        // Se alguem adicionar um TipoDeRegra e esquecer do serializador, a
        // falha aparece aqui, e nao ao ler uma regra ja gravada em producao.
        var tiposCobertos = Amostras.Select(c => c.Tipo).Order().ToList();

        Assert.Equal(Enum.GetValues<TipoDeRegra>().Order().ToList(), tiposCobertos);
    }

    [Theory]
    [InlineData("{\"tipo\":\"RegraInventada\",\"maximoDeTransacoes\":3}")]
    [InlineData("{\"tipo\":\"\",\"maximoDeTransacoes\":3}")]
    [InlineData("{\"tipo\":\"velocidadePorCliente\"}")]
    [InlineData("{\"maximoDeTransacoes\":3}")]
    public void Tipo_desconhecido_ou_ausente_falha_alto(string json)
    {
        // O catalogo e fechado. Uma linha adulterada no banco nao vira
        // comportamento novo: vira erro. E o caso "velocidadePorCliente" em
        // minusculas esta aqui de proposito — a leitura do tipo e sensivel a
        // maiusculas, para que so o nome exato do enum passe.
        Assert.Throws<InvalidOperationException>(
            () => SerializadorDeConfiguracaoDeRegra.Desserializar(json));
    }

    [Fact]
    public void Json_gravado_carrega_o_discriminador_de_tipo()
    {
        // O tipo viaja DENTRO do JSON porque o conversor do EF so enxerga a
        // propria coluna. Sem o discriminador, a leitura nao teria como
        // escolher qual record reconstruir.
        var json = SerializadorDeConfiguracaoDeRegra.Serializar(
            new ConfiguracaoDeVelocidade(MaximoDeTransacoes: 3, JanelaEmMinutos: 10));

        Assert.Contains("\"tipo\":\"VelocidadePorCliente\"", json, StringComparison.Ordinal);
    }
}
