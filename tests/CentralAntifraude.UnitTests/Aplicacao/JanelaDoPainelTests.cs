using CentralAntifraude.Application.Operacao;

namespace CentralAntifraude.UnitTests.Aplicacao;

/// <summary>
/// A janela do painel nos dois caminhos: numero de dias ancorado em "agora" e
/// intervalo absoluto.
///
/// O intervalo absoluto existe para o salto "ver o periodo com atividade" — e o
/// que faz a demo nao abrir vazia quando o cenario envelhece. Por isso ele
/// precisa recusar, e nao consertar em silencio, tudo que produziria numeros de
/// um periodo diferente do pedido: intervalo invertido, futuro, ou mais largo
/// que o teto. O teto e o mesmo do caminho por dias, para o botao da tela nunca
/// pedir um intervalo que o painel rejeitaria.
/// </summary>
public class JanelaDoPainelTests
{
    private static readonly DateTimeOffset Agora =
        new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sem_dias_informados_usa_o_padrao()
    {
        Assert.True(JanelaDoPainel.TentarCriar(null, Agora, out var janela, out _));
        Assert.Equal(JanelaDoPainel.DiasPadrao, janela.Dias);
        Assert.Equal(Agora, janela.Ate);
        Assert.Equal(Agora.AddDays(-JanelaDoPainel.DiasPadrao), janela.De);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(JanelaDoPainel.DiasMaximos + 1)]
    public void Dias_fora_da_faixa_e_recusado(int dias)
    {
        Assert.False(JanelaDoPainel.TentarCriar(dias, Agora, out _, out var erro));
        Assert.NotEmpty(erro);
    }

    [Fact]
    public void Intervalo_absoluto_valido_vira_janela()
    {
        var de = Agora.AddDays(-30);

        Assert.True(JanelaDoPainel.TentarCriarComIntervalo(de, Agora, Agora, out var janela, out _));
        Assert.Equal(de, janela.De);
        Assert.Equal(Agora, janela.Ate);
        Assert.Equal(30, janela.Dias);
    }

    [Fact]
    public void Intervalo_invertido_e_recusado()
    {
        var de = Agora;
        var ate = Agora.AddDays(-1);

        Assert.False(JanelaDoPainel.TentarCriarComIntervalo(de, ate, Agora, out _, out var erro));
        Assert.NotEmpty(erro);
    }

    [Fact]
    public void Intervalo_no_futuro_e_recusado()
    {
        var de = Agora.AddDays(-1);
        var ate = Agora.AddDays(2);

        Assert.False(JanelaDoPainel.TentarCriarComIntervalo(de, ate, Agora, out _, out var erro));
        Assert.NotEmpty(erro);
    }

    [Fact]
    public void Intervalo_alem_do_teto_e_recusado()
    {
        // Um dia a mais que o teto ja basta para recusar: o painel responde um
        // horizonte definido, nao um arquivo historico.
        var de = Agora.AddDays(-(JanelaDoPainel.DiasMaximos + 1));

        Assert.False(JanelaDoPainel.TentarCriarComIntervalo(de, Agora, Agora, out _, out var erro));
        Assert.NotEmpty(erro);
    }

    [Fact]
    public void Intervalo_no_teto_exato_e_aceito()
    {
        // A fronteira precisa passar: o salto da tela mira exatamente os ultimos
        // DiasMaximos dias de atividade, e nao pode cair do lado recusado.
        var de = Agora.AddDays(-JanelaDoPainel.DiasMaximos);

        Assert.True(JanelaDoPainel.TentarCriarComIntervalo(de, Agora, Agora, out var janela, out _));
        Assert.Equal(de, janela.De);
    }
}
