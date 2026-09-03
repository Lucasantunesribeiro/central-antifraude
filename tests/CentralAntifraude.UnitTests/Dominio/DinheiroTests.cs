using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// Protege a decisao de dinheiro congelada na Fase 0 (CLAUDE.md secao 101).
/// </summary>
public sealed class DinheiroTests
{
    [Fact]
    public void Cria_valor_valido_e_normaliza_a_moeda_para_maiusculas()
    {
        var dinheiro = Dinheiro.De(1234.56m, "brl");

        Assert.Equal(1234.56m, dinheiro.Valor);
        Assert.Equal("BRL", dinheiro.Moeda);
        Assert.True(dinheiro.EhValido);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BR")]
    [InlineData("BRLL")]
    [InlineData("98")]
    [InlineData("BR1")]
    [InlineData("R$")]
    public void Recusa_moeda_fora_do_formato_iso_4217(string moeda)
    {
        var criou = Dinheiro.TentarCriar(10m, moeda, out _, out var erro);

        Assert.False(criou);
        Assert.NotEmpty(erro);
    }

    [Fact]
    public void Recusa_moeda_nula()
    {
        Assert.False(Dinheiro.TentarCriar(10m, null, out _, out _));
    }

    [Fact]
    public void Aceita_ate_quatro_casas_decimais()
    {
        Assert.True(Dinheiro.TentarCriar(10.1234m, "USD", out _, out _));
    }

    [Fact]
    public void Recusa_mais_casas_decimais_do_que_a_coluna_suporta()
    {
        // Sem esta guarda, 10.12345 seria silenciosamente arredondado pela
        // coluna numeric(18,4) na hora de gravar - e um valor de transacao
        // gravado diferente do valor recebido e defeito grave num sistema
        // antifraude.
        var criou = Dinheiro.TentarCriar(10.12345m, "USD", out _, out var erro);

        Assert.False(criou);
        Assert.Contains("casas decimais", erro, StringComparison.Ordinal);
    }

    [Fact]
    public void De_lanca_violacao_de_invariante_quando_a_entrada_e_invalida()
    {
        Assert.Throws<ViolacaoDeInvariante>(() => Dinheiro.De(10m, "invalido"));
    }

    [Fact]
    public void Instancia_padrao_nao_e_valida()
    {
        // default(Dinheiro) nunca passou por validacao: nao tem moeda.
        // Quem receber um Dinheiro de origem duvidosa precisa perguntar.
        var naoInicializado = default(Dinheiro);

        Assert.False(naoInicializado.EhValido);
        Assert.Equal(string.Empty, naoInicializado.Moeda);
    }

    [Fact]
    public void Dois_valores_iguais_sao_iguais()
    {
        Assert.Equal(Dinheiro.De(99.90m, "BRL"), Dinheiro.De(99.90m, "brl"));
    }

    [Fact]
    public void Mesma_quantia_em_moedas_diferentes_nao_e_o_mesmo_valor()
    {
        Assert.NotEqual(Dinheiro.De(100m, "BRL"), Dinheiro.De(100m, "USD"));
    }
}
