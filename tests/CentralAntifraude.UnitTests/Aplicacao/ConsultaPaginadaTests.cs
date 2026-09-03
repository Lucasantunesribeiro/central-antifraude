using CentralAntifraude.Application.Comum;

namespace CentralAntifraude.UnitTests.Aplicacao;

public sealed class ParametrosDePaginacaoTests
{
    [Fact]
    public void Sem_parametros_usa_o_padrao()
    {
        Assert.True(ParametrosDePaginacao.TentarCriar(null, null, out var parametros, out _));

        Assert.Equal(1, parametros.Pagina);
        Assert.Equal(ParametrosDePaginacao.TamanhoPadrao, parametros.Tamanho);
        Assert.Equal(0, parametros.QuantidadeAPular);
    }

    [Fact]
    public void Calcula_o_deslocamento_da_pagina()
    {
        Assert.True(ParametrosDePaginacao.TentarCriar(3, 20, out var parametros, out _));

        Assert.Equal(40, parametros.QuantidadeAPular);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Recusa_pagina_fora_da_faixa(int pagina)
    {
        Assert.False(ParametrosDePaginacao.TentarCriar(pagina, null, out _, out var erro));
        Assert.NotEmpty(erro);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void Recusa_tamanho_fora_da_faixa(int tamanho)
    {
        // Um pedido de 5.000 registros e recusado, nao reduzido em silencio:
        // quem pede uma pagina gigante precisa saber que nao a recebeu.
        Assert.False(ParametrosDePaginacao.TentarCriar(null, tamanho, out _, out var erro));
        Assert.NotEmpty(erro);
    }

    [Fact]
    public void Aceita_exatamente_o_tamanho_maximo()
    {
        Assert.True(ParametrosDePaginacao.TentarCriar(
            1,
            ParametrosDePaginacao.TamanhoMaximo,
            out var parametros,
            out _));

        Assert.Equal(ParametrosDePaginacao.TamanhoMaximo, parametros.Tamanho);
    }
}

public sealed class ParametrosDeOrdenacaoTests
{
    private static readonly string[] CamposPermitidos = ["ocorridoEm", "valor", "score"];

    [Fact]
    public void Sem_campo_pedido_usa_o_padrao_da_consulta()
    {
        Assert.True(ParametrosDeOrdenacao.TentarCriar(
            campo: null,
            direcao: null,
            CamposPermitidos,
            campoPadrao: "ocorridoEm",
            out var parametros,
            out _));

        Assert.Equal("ocorridoEm", parametros.Campo);
        Assert.Equal(DirecaoDeOrdenacao.Descendente, parametros.Direcao);
    }

    [Fact]
    public void Devolve_o_nome_canonico_e_nao_o_texto_do_cliente()
    {
        // O que sai daqui e o nome da lista de permitidos, nunca a string
        // recebida. E o que impede texto do cliente de alcancar a montagem
        // da consulta.
        Assert.True(ParametrosDeOrdenacao.TentarCriar(
            campo: "  VaLoR  ",
            direcao: "asc",
            CamposPermitidos,
            campoPadrao: "ocorridoEm",
            out var parametros,
            out _));

        Assert.Equal("valor", parametros.Campo);
        Assert.Equal(DirecaoDeOrdenacao.Ascendente, parametros.Direcao);
    }

    [Theory]
    [InlineData("senha")]
    [InlineData("id; drop table transacoes")]
    [InlineData("tenant_id")]
    [InlineData("(select 1)")]
    public void Recusa_campo_fora_da_lista_de_permitidos(string campo)
    {
        Assert.False(ParametrosDeOrdenacao.TentarCriar(
            campo,
            direcao: null,
            CamposPermitidos,
            campoPadrao: "ocorridoEm",
            out _,
            out var erro));

        Assert.NotEmpty(erro);
    }

    [Fact]
    public void Recusa_direcao_desconhecida()
    {
        Assert.False(ParametrosDeOrdenacao.TentarCriar(
            campo: "valor",
            direcao: "aleatorio",
            CamposPermitidos,
            campoPadrao: "ocorridoEm",
            out _,
            out _));
    }

    [Fact]
    public void Campo_padrao_precisa_estar_entre_os_permitidos()
    {
        // Erro de programacao, nao de entrada: falha alto para nao deixar
        // passar uma consulta com padrao invalido.
        Assert.Throws<ArgumentException>(() => ParametrosDeOrdenacao.TentarCriar(
            campo: null,
            direcao: null,
            CamposPermitidos,
            campoPadrao: "campoInexistente",
            out _,
            out _));
    }
}

public sealed class PaginaTests
{
    [Fact]
    public void Calcula_total_de_paginas_arredondando_para_cima()
    {
        ParametrosDePaginacao.TentarCriar(1, 25, out var parametros, out _);

        var pagina = new Pagina<int>([1, 2, 3], parametros, totalDeItens: 51);

        Assert.Equal(3, pagina.TotalDePaginas);
        Assert.True(pagina.TemProximaPagina);
    }

    [Fact]
    public void Sem_itens_nao_ha_paginas_nem_proxima()
    {
        ParametrosDePaginacao.TentarCriar(1, 25, out var parametros, out _);

        var pagina = new Pagina<int>([], parametros, totalDeItens: 0);

        Assert.Equal(0, pagina.TotalDePaginas);
        Assert.False(pagina.TemProximaPagina);
    }

    [Fact]
    public void Ultima_pagina_nao_anuncia_proxima()
    {
        ParametrosDePaginacao.TentarCriar(3, 25, out var parametros, out _);

        var pagina = new Pagina<int>([1], parametros, totalDeItens: 51);

        Assert.False(pagina.TemProximaPagina);
    }
}
