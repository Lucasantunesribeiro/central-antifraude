using System.Globalization;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// Cada regra, isolada.
///
/// O desenho dos casos e sempre o mesmo, porque o risco tambem e o mesmo:
/// uma regra que dispara cedo demais enche a fila do analista de falso
/// positivo, e uma que dispara tarde demais deixa fraude passar. Por isso todo
/// avaliador e testado no limite EXATO, um passo abaixo e um passo acima — e
/// nao apenas em um caso obvio no meio do intervalo.
///
/// O outro eixo e a ausencia de dado: falta de fingerprint, falta de pais,
/// historico curto. A resposta correta em todos e o silencio. Transformar
/// "nao sei" em "suspeito" seria produzir risco a partir de ignorancia.
/// </summary>
public class AvaliadoresDeRegraTests
{
    // -----------------------------------------------------------------------
    // Velocidade
    // -----------------------------------------------------------------------

    private static readonly ConfiguracaoDeVelocidade Velocidade =
        new(MaximoDeTransacoes: 3, JanelaEmMinutos: 10);

    [Fact]
    public void Velocidade_nao_dispara_quando_o_total_iguala_o_limite()
    {
        // Duas anteriores + a atual = 3, exatamente o maximo configurado.
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(minutosAntes: 1),
            CenarioDeRisco.Anterior(minutosAntes: 2));

        var sinal = new AvaliadorDeVelocidade()
            .Avaliar(Velocidade, CenarioDeRisco.Transacao(), contexto);

        Assert.Null(sinal);
    }

    [Fact]
    public void Velocidade_dispara_no_primeiro_passo_acima_do_limite()
    {
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(minutosAntes: 1),
            CenarioDeRisco.Anterior(minutosAntes: 2),
            CenarioDeRisco.Anterior(minutosAntes: 3));

        var sinal = new AvaliadorDeVelocidade()
            .Avaliar(Velocidade, CenarioDeRisco.Transacao(), contexto);

        Assert.NotNull(sinal);
        Assert.Equal(TipoDeRegra.VelocidadePorCliente, sinal.Tipo);
        Assert.Equal("4", sinal.DadosDaEvidencia["tentativasNaJanela"]);
        Assert.Equal("3", sinal.DadosDaEvidencia["limiteConfigurado"]);
    }

    [Fact]
    public void Velocidade_conta_a_transacao_avaliada_junto_das_anteriores()
    {
        // Tres anteriores, uma atual: o operador que configurou "maximo 3"
        // espera que a quarta dispare. Se a regra contasse so as anteriores,
        // o limite real seria 4 e o numero configurado mentiria.
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(minutosAntes: 1),
            CenarioDeRisco.Anterior(minutosAntes: 2),
            CenarioDeRisco.Anterior(minutosAntes: 3));

        var sinal = new AvaliadorDeVelocidade()
            .Avaliar(Velocidade, CenarioDeRisco.Transacao(), contexto);

        Assert.Equal("4", sinal!.DadosDaEvidencia["tentativasNaJanela"]);
    }

    [Fact]
    public void Velocidade_ignora_transacoes_fora_da_janela()
    {
        // Quatro anteriores, mas tres delas fora dos 10 minutos.
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(minutosAntes: 1),
            CenarioDeRisco.Anterior(minutosAntes: 30),
            CenarioDeRisco.Anterior(minutosAntes: 60),
            CenarioDeRisco.Anterior(minutosAntes: 90));

        var sinal = new AvaliadorDeVelocidade()
            .Avaliar(Velocidade, CenarioDeRisco.Transacao(), contexto);

        Assert.Null(sinal);
    }

    [Fact]
    public void Velocidade_trata_a_borda_da_janela_como_intervalo_semiaberto()
    {
        // Exatamente 10 minutos antes fica FORA; 9,999 fica dentro. A escolha
        // precisa ser uma so e testada: sem isso, o comportamento na borda
        // dependeria de arredondamento e a mesma rajada poderia disparar ou
        // nao conforme o milissegundo.
        var noLimite = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(minutosAntes: 10),
            CenarioDeRisco.Anterior(minutosAntes: 1),
            CenarioDeRisco.Anterior(minutosAntes: 2));

        Assert.Null(new AvaliadorDeVelocidade()
            .Avaliar(Velocidade, CenarioDeRisco.Transacao(), noLimite));

        var dentro = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(minutosAntes: 9.99),
            CenarioDeRisco.Anterior(minutosAntes: 1),
            CenarioDeRisco.Anterior(minutosAntes: 2));

        Assert.NotNull(new AvaliadorDeVelocidade()
            .Avaliar(Velocidade, CenarioDeRisco.Transacao(), dentro));
    }

    [Fact]
    public void Velocidade_ancora_a_janela_no_momento_da_transacao_e_nao_no_agora()
    {
        // Uma rajada que aconteceu ha dois dias e chega atrasada continua
        // sendo uma rajada: as quatro transacoes estao a minutos uma da outra.
        // Ancorar em "agora" faria o grupo inteiro cair fora da janela e a
        // fraude passaria despercebida so por causa do atraso (CLAUDE.md
        // secao 16).
        var doisDiasAntes = CenarioDeRisco.Referencia.AddDays(-2);

        var contexto = new ContextoDeRisco(
        [
            new TransacaoDoHistorico(doisDiasAntes.AddMinutes(-1), 100m, "BRL", "disp", "BR"),
            new TransacaoDoHistorico(doisDiasAntes.AddMinutes(-2), 100m, "BRL", "disp", "BR"),
            new TransacaoDoHistorico(doisDiasAntes.AddMinutes(-3), 100m, "BRL", "disp", "BR"),
        ]);

        var sinal = new AvaliadorDeVelocidade().Avaliar(
            Velocidade,
            CenarioDeRisco.Transacao(ocorridaEm: doisDiasAntes),
            contexto);

        Assert.NotNull(sinal);
    }

    [Fact]
    public void Velocidade_fica_calada_sem_historico()
    {
        var sinal = new AvaliadorDeVelocidade()
            .Avaliar(Velocidade, CenarioDeRisco.Transacao(), ContextoDeRisco.Vazio);

        Assert.Null(sinal);
    }

    // -----------------------------------------------------------------------
    // Novo dispositivo
    // -----------------------------------------------------------------------

    private static readonly ConfiguracaoDeNovoDispositivo NovoDispositivo =
        new(MinimoDeTransacoesNoHistorico: 3);

    [Fact]
    public void NovoDispositivo_dispara_com_historico_suficiente_e_aparelho_desconhecido()
    {
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, dispositivo: "disp-a"),
            CenarioDeRisco.Anterior(2, dispositivo: "disp-a"),
            CenarioDeRisco.Anterior(3, dispositivo: "disp-b"));

        var sinal = new AvaliadorDeNovoDispositivo().Avaliar(
            NovoDispositivo,
            CenarioDeRisco.Transacao(dispositivo: "disp-nunca-visto"),
            contexto);

        Assert.NotNull(sinal);
        Assert.Equal("3", sinal.DadosDaEvidencia["transacoesNoHistorico"]);
    }

    [Fact]
    public void NovoDispositivo_fica_calado_um_passo_abaixo_do_historico_minimo()
    {
        // Duas anteriores, minimo tres. Para quem quase nao tem historico,
        // TODO dispositivo e novo e o sinal nao separaria nada.
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, dispositivo: "disp-a"),
            CenarioDeRisco.Anterior(2, dispositivo: "disp-a"));

        var sinal = new AvaliadorDeNovoDispositivo().Avaliar(
            NovoDispositivo,
            CenarioDeRisco.Transacao(dispositivo: "disp-nunca-visto"),
            contexto);

        Assert.Null(sinal);
    }

    [Fact]
    public void NovoDispositivo_nao_dispara_para_aparelho_ja_visto()
    {
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, dispositivo: "disp-a"),
            CenarioDeRisco.Anterior(2, dispositivo: "disp-b"),
            CenarioDeRisco.Anterior(3, dispositivo: "disp-c"));

        var sinal = new AvaliadorDeNovoDispositivo().Avaliar(
            NovoDispositivo,
            CenarioDeRisco.Transacao(dispositivo: "disp-b"),
            contexto);

        Assert.Null(sinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NovoDispositivo_fica_calado_sem_fingerprint(string? fingerprint)
    {
        // Ausencia de dado nao e evidencia. Tratar "nao informado" como
        // "aparelho novo" faria toda integracao que nao envia o campo gerar
        // risco em toda transacao.
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, dispositivo: "disp-a"),
            CenarioDeRisco.Anterior(2, dispositivo: "disp-a"),
            CenarioDeRisco.Anterior(3, dispositivo: "disp-a"));

        var sinal = new AvaliadorDeNovoDispositivo().Avaliar(
            NovoDispositivo,
            CenarioDeRisco.Transacao(dispositivo: fingerprint),
            contexto);

        Assert.Null(sinal);
    }

    // -----------------------------------------------------------------------
    // Valor acima do historico
    // -----------------------------------------------------------------------

    private static readonly ConfiguracaoDeValorAcimaDoHistorico ValorAcima =
        new(MultiploDaMedia: 5m, MinimoDeTransacoesNoHistorico: 3);

    [Fact]
    public void ValorAcimaDoHistorico_nao_dispara_no_limite_exato()
    {
        // Media 100, multiplo 5, limite 500. Exatamente 500 nao dispara: a
        // comparacao e estritamente "acima".
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, valor: 100m),
            CenarioDeRisco.Anterior(2, valor: 100m),
            CenarioDeRisco.Anterior(3, valor: 100m));

        var sinal = new AvaliadorDeValorAcimaDoHistorico().Avaliar(
            ValorAcima,
            CenarioDeRisco.Transacao(valor: 500m),
            contexto);

        Assert.Null(sinal);
    }

    [Fact]
    public void ValorAcimaDoHistorico_dispara_um_centavo_acima_do_limite()
    {
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, valor: 100m),
            CenarioDeRisco.Anterior(2, valor: 100m),
            CenarioDeRisco.Anterior(3, valor: 100m));

        var sinal = new AvaliadorDeValorAcimaDoHistorico().Avaliar(
            ValorAcima,
            CenarioDeRisco.Transacao(valor: 500.01m),
            contexto);

        Assert.NotNull(sinal);
        Assert.Equal("100.0000", sinal.DadosDaEvidencia["mediaHistorica"]);
        Assert.Equal("500.0000", sinal.DadosDaEvidencia["limiteConfigurado"]);
        Assert.Equal("BRL", sinal.DadosDaEvidencia["moeda"]);
    }

    [Fact]
    public void ValorAcimaDoHistorico_compara_apenas_dentro_da_mesma_moeda()
    {
        // Historico inteiro em USD, transacao em BRL: nao ha media em BRL, e
        // comparar 5000 BRL com uma media de dolares produziria um numero sem
        // significado nenhum.
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, valor: 10m, moeda: "USD"),
            CenarioDeRisco.Anterior(2, valor: 10m, moeda: "USD"),
            CenarioDeRisco.Anterior(3, valor: 10m, moeda: "USD"));

        var sinal = new AvaliadorDeValorAcimaDoHistorico().Avaliar(
            ValorAcima,
            CenarioDeRisco.Transacao(valor: 5_000m, moeda: "BRL"),
            contexto);

        Assert.Null(sinal);
    }

    [Fact]
    public void ValorAcimaDoHistorico_fica_calado_um_passo_abaixo_do_historico_minimo()
    {
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, valor: 100m),
            CenarioDeRisco.Anterior(2, valor: 100m));

        var sinal = new AvaliadorDeValorAcimaDoHistorico().Avaliar(
            ValorAcima,
            CenarioDeRisco.Transacao(valor: 99_999m),
            contexto);

        Assert.Null(sinal);
    }

    [Fact]
    public void ValorAcimaDoHistorico_usa_media_e_nao_maximo()
    {
        // Uma unica compra atipica de 1000 no meio de compras de 100: a media
        // (280) mantem a regra util. Se comparasse com o maximo historico, uma
        // unica compra grande calaria a regra para sempre.
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, valor: 100m),
            CenarioDeRisco.Anterior(2, valor: 1_000m),
            CenarioDeRisco.Anterior(3, valor: 100m),
            CenarioDeRisco.Anterior(4, valor: 100m),
            CenarioDeRisco.Anterior(5, valor: 100m));

        var media = 280m;
        var sinal = new AvaliadorDeValorAcimaDoHistorico().Avaliar(
            ValorAcima,
            CenarioDeRisco.Transacao(valor: (media * 5m) + 1m),
            contexto);

        Assert.NotNull(sinal);
        Assert.Equal(
            media.ToString("0.0000", CultureInfo.InvariantCulture),
            sinal.DadosDaEvidencia["mediaHistorica"]);
    }

    // -----------------------------------------------------------------------
    // Divergencia geografica
    // -----------------------------------------------------------------------

    private static readonly ConfiguracaoDeDivergenciaGeografica Divergencia =
        new(MinimoDeTransacoesNoHistorico: 3);

    [Fact]
    public void DivergenciaGeografica_dispara_para_pais_novo()
    {
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, pais: "BR"),
            CenarioDeRisco.Anterior(2, pais: "BR"),
            CenarioDeRisco.Anterior(3, pais: "PT"));

        var sinal = new AvaliadorDeDivergenciaGeografica().Avaliar(
            Divergencia,
            CenarioDeRisco.Transacao(pais: "RU"),
            contexto);

        Assert.NotNull(sinal);
        Assert.Equal("RU", sinal.DadosDaEvidencia["paisDaTransacao"]);
        Assert.Equal("BR,PT", sinal.DadosDaEvidencia["paisesConhecidos"]);
    }

    [Fact]
    public void DivergenciaGeografica_nao_dispara_para_pais_ja_visto()
    {
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, pais: "BR"),
            CenarioDeRisco.Anterior(2, pais: "PT"),
            CenarioDeRisco.Anterior(3, pais: "BR"));

        var sinal = new AvaliadorDeDivergenciaGeografica().Avaliar(
            Divergencia,
            CenarioDeRisco.Transacao(pais: "PT"),
            contexto);

        Assert.Null(sinal);
    }

    [Fact]
    public void DivergenciaGeografica_fica_calada_quando_o_historico_nao_tem_pais()
    {
        // Historico suficiente em quantidade, mas nenhuma anterior trouxe o
        // campo. Nao existe padrao geografico do qual divergir.
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, pais: null),
            CenarioDeRisco.Anterior(2, pais: null),
            CenarioDeRisco.Anterior(3, pais: null));

        var sinal = new AvaliadorDeDivergenciaGeografica().Avaliar(
            Divergencia,
            CenarioDeRisco.Transacao(pais: "RU"),
            contexto);

        Assert.Null(sinal);
    }

    [Fact]
    public void DivergenciaGeografica_fica_calada_sem_pais_na_transacao()
    {
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, pais: "BR"),
            CenarioDeRisco.Anterior(2, pais: "BR"),
            CenarioDeRisco.Anterior(3, pais: "BR"));

        var sinal = new AvaliadorDeDivergenciaGeografica().Avaliar(
            Divergencia,
            CenarioDeRisco.Transacao(pais: null),
            contexto);

        Assert.Null(sinal);
    }

    [Fact]
    public void DivergenciaGeografica_fica_calada_um_passo_abaixo_do_historico_minimo()
    {
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(1, pais: "BR"),
            CenarioDeRisco.Anterior(2, pais: "BR"));

        var sinal = new AvaliadorDeDivergenciaGeografica().Avaliar(
            Divergencia,
            CenarioDeRisco.Transacao(pais: "RU"),
            contexto);

        Assert.Null(sinal);
    }

    // -----------------------------------------------------------------------
    // Contrato comum
    // -----------------------------------------------------------------------

    public static TheoryData<IAvaliadorDeRegra, ConfiguracaoDeRegra> AvaliadorComConfiguracaoDeOutroTipo =>
        new()
        {
            { new AvaliadorDeVelocidade(), NovoDispositivo },
            { new AvaliadorDeNovoDispositivo(), Velocidade },
            { new AvaliadorDeValorAcimaDoHistorico(), Divergencia },
            { new AvaliadorDeDivergenciaGeografica(), ValorAcima },
        };

    [Theory]
    [MemberData(nameof(AvaliadorComConfiguracaoDeOutroTipo))]
    public void Avaliador_recusa_configuracao_de_outro_tipo(
        IAvaliadorDeRegra avaliador,
        ConfiguracaoDeRegra configuracaoErrada)
    {
        // Nao ha coercao nem valor padrao inventado: configuracao do tipo
        // errado nao produz sinal. Uma regra que "chutasse" limites diante de
        // configuracao incompativel avaliaria com numeros que ninguem definiu.
        var sinal = avaliador.Avaliar(
            configuracaoErrada,
            CenarioDeRisco.Transacao(),
            ContextoDeRisco.Vazio);

        Assert.Null(sinal);
    }
}
