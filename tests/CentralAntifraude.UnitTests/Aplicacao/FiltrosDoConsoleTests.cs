using CentralAntifraude.Application.Auditoria;
using CentralAntifraude.Application.Operacao;
using CentralAntifraude.Application.Transacoes;
using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.UnitTests.Aplicacao;

/// <summary>
/// Os filtros do console operacional.
///
/// **O que estes testes protegem e a fronteira entre texto do cliente e
/// consulta.** Decisao e tipo de regra viram valores de enum ou recusa; numeros
/// passam por faixa; e a busca livre — a unica entrada de texto que chega ao
/// banco — vai como literal, com os curingas de `LIKE` escapados.
///
/// Recusar em vez de ignorar e a regra: um filtro descartado em silencio
/// devolveria a lista inteira, e quem consultou acreditaria estar vendo so os
/// bloqueios.
/// </summary>
public class FiltrosDoConsoleTests
{
    private static bool TentarTransacoes(
        out FiltroDeTransacoes filtro,
        out string erro,
        string? busca = null,
        string? decisao = null,
        string? tipoDeRegra = null,
        int? scoreMinimo = null,
        int? scoreMaximo = null,
        DateTimeOffset? de = null,
        DateTimeOffset? ate = null) =>
        FiltroDeTransacoes.TentarCriar(
            busca,
            decisao,
            tipoDeRegra,
            scoreMinimo,
            scoreMaximo,
            de,
            ate,
            out filtro,
            out erro);

    // -----------------------------------------------------------------------
    // Transacoes
    // -----------------------------------------------------------------------

    [Fact]
    public void Sem_nada_informado_o_filtro_fica_vazio()
    {
        Assert.True(TentarTransacoes(out var filtro, out _));

        Assert.True(filtro.EstaVazio);
        Assert.False(filtro.ExigeAvaliacao);
        Assert.Null(filtro.PadraoDeBusca);
    }

    [Theory]
    [InlineData("Revisar", Decisao.Revisar)]
    [InlineData("revisar", Decisao.Revisar)]
    [InlineData("BLOQUEAR", Decisao.Bloquear)]
    public void Decisao_e_resolvida_pelo_nome_em_qualquer_caixa(string texto, Decisao esperada)
    {
        Assert.True(TentarTransacoes(out var filtro, out _, decisao: texto));

        Assert.Equal(esperada, filtro.Decisao);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("99")]
    [InlineData("Aprovar")]
    public void Decisao_fora_do_vocabulario_e_recusada(string texto)
    {
        // O numero tambem: `Enum.TryParse` aceitaria "2" como Revisar e "99"
        // como um valor que nao existe no enum. Comparar com os NOMES fecha
        // os dois buracos.
        Assert.False(TentarTransacoes(out _, out var erro, decisao: texto));
        Assert.Contains("decisao", erro, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tipo_de_regra_fora_do_catalogo_e_recusado()
    {
        Assert.False(TentarTransacoes(out _, out var erro, tipoDeRegra: "RegraMagica"));
        Assert.Contains("tipoDeRegra", erro, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, 101)]
    [InlineData(80, 20)]
    public void Faixa_de_score_impossivel_e_recusada(int? minimo, int? maximo)
    {
        // Faixa invertida devolveria zero linha e pareceria "nao ha nada
        // aqui", quando na verdade a pergunta e que estava errada.
        Assert.False(TentarTransacoes(out _, out var erro, scoreMinimo: minimo, scoreMaximo: maximo));
        Assert.NotEmpty(erro);
    }

    [Fact]
    public void Periodo_invertido_e_recusado()
    {
        var agora = DateTimeOffset.UtcNow;

        Assert.False(TentarTransacoes(out _, out _, de: agora, ate: agora.AddDays(-1)));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("")]
    public void Busca_curta_demais_e_recusada(string texto)
    {
        // Vazio cai no "sem filtro"; uma letra so casaria com quase tudo e
        // faria a consulta varrer a tabela para devolver um resultado inutil.
        var resultado = TentarTransacoes(out var filtro, out _, busca: texto);

        if (texto.Length == 0)
        {
            Assert.True(resultado);
            Assert.Null(filtro.Busca);
        }
        else
        {
            Assert.False(resultado);
        }
    }

    [Fact]
    public void Busca_longa_demais_e_recusada()
    {
        Assert.False(TentarTransacoes(
            out _,
            out _,
            busca: new string('x', FiltroDeTransacoes.TamanhoMaximoDaBusca + 1)));
    }

    [Fact]
    public void Busca_e_aparada_antes_de_virar_padrao()
    {
        Assert.True(TentarTransacoes(out var filtro, out _, busca: "  pedido-1  "));

        Assert.Equal("pedido-1", filtro.Busca);
        Assert.Equal("%pedido-1%", filtro.PadraoDeBusca);
    }

    [Theory]
    [InlineData("100%", "%100\\%%")]
    [InlineData("a_b", "%a\\_b%")]
    [InlineData("c\\d", "%c\\\\d%")]
    public void Curingas_digitados_pela_pessoa_viram_texto(string busca, string padraoEsperado)
    {
        // **Este e o teste que importa na busca livre.** Sem o escape, um `%`
        // digitado por engano devolveria a tabela inteira e a tela pareceria
        // filtrada — pior do que um erro, porque ninguem desconfia de uma
        // lista que voltou cheia.
        Assert.True(TentarTransacoes(out var filtro, out _, busca: busca));

        Assert.Equal(padraoEsperado, filtro.PadraoDeBusca);
    }

    [Fact]
    public void Filtro_de_avaliacao_e_reconhecido_como_tal()
    {
        // Decide entre juncao a esquerda e interna: filtrar por score exclui,
        // por definicao, as transacoes que nunca foram avaliadas.
        Assert.True(TentarTransacoes(out var comBusca, out _, busca: "pedido"));
        Assert.False(comBusca.ExigeAvaliacao);

        Assert.True(TentarTransacoes(out var comScore, out _, scoreMinimo: 40));
        Assert.True(comScore.ExigeAvaliacao);

        Assert.True(TentarTransacoes(out var comDecisao, out _, decisao: "Bloquear"));
        Assert.True(comDecisao.ExigeAvaliacao);

        Assert.True(TentarTransacoes(
            out var comSinal,
            out _,
            tipoDeRegra: nameof(TipoDeRegra.NovoDispositivo)));

        Assert.True(comSinal.ExigeAvaliacao);
    }

    // -----------------------------------------------------------------------
    // Auditoria
    // -----------------------------------------------------------------------

    [Fact]
    public void Operacao_auditada_e_resolvida_pelo_nome()
    {
        Assert.True(FiltroDeAuditoria.TentarCriar(
            "VersaoDeRegraPublicada",
            null,
            null,
            null,
            null,
            out var filtro,
            out _));

        Assert.Equal(OperacaoAuditada.VersaoDeRegraPublicada, filtro.Operacao);
    }

    [Theory]
    [InlineData("63")]
    [InlineData("ApagarTudo")]
    public void Operacao_fora_do_vocabulario_e_recusada(string texto)
    {
        Assert.False(FiltroDeAuditoria.TentarCriar(
            texto,
            null,
            null,
            null,
            null,
            out _,
            out var erro));

        Assert.Contains("operacao", erro, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Periodo_invertido_na_auditoria_e_recusado()
    {
        var agora = DateTimeOffset.UtcNow;

        Assert.False(FiltroDeAuditoria.TentarCriar(
            null,
            null,
            null,
            agora,
            agora.AddDays(-1),
            out _,
            out _));
    }

    // -----------------------------------------------------------------------
    // Janela do painel
    // -----------------------------------------------------------------------

    [Fact]
    public void Janela_sem_parametro_cai_no_padrao()
    {
        var agora = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

        Assert.True(JanelaDoPainel.TentarCriar(null, agora, out var janela, out _));

        Assert.Equal(JanelaDoPainel.DiasPadrao, janela.Dias);
        Assert.Equal(agora, janela.Ate);
        Assert.Equal(agora.AddDays(-JanelaDoPainel.DiasPadrao), janela.De);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(JanelaDoPainel.DiasMaximos + 1)]
    public void Janela_fora_da_faixa_e_recusada(int dias)
    {
        // Recusar, e nao limitar em silencio: um pedido de dez anos reduzido
        // para noventa dias devolveria numeros que nao respondem a pergunta
        // feita.
        Assert.False(JanelaDoPainel.TentarCriar(
            dias,
            DateTimeOffset.UtcNow,
            out _,
            out var erro));

        Assert.Contains("painel", erro, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Janela_no_limite_e_aceita()
    {
        Assert.True(JanelaDoPainel.TentarCriar(
            JanelaDoPainel.DiasMaximos,
            DateTimeOffset.UtcNow,
            out var janela,
            out _));

        Assert.Equal(JanelaDoPainel.DiasMaximos, janela.Dias);
    }
}
