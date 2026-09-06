using CentralAntifraude.Application.Alertas;
using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.UnitTests.Aplicacao;

/// <summary>
/// Os filtros da fila operacional.
///
/// **A regra unica desta classe: recusar, nunca ignorar.** Um filtro
/// desconhecido silenciosamente descartado devolveria a lista inteira, e o
/// analista acreditaria estar vendo so os bloqueios. Numa fila de fraude,
/// acreditar que se filtrou e pior do que receber um erro — e a mesma decisao
/// que a paginacao ja tomava desde a Fase 0 ao recusar um pedido de 5.000
/// registros em vez de reduzi-lo em silencio.
/// </summary>
public class FiltroDeAlertasTests
{
    [Fact]
    public void Sem_nenhum_parametro_o_filtro_fica_vazio()
    {
        Assert.True(FiltroDeAlertas.TentarCriar(null, null, null, null, null, null, out var filtro, out var erro));

        Assert.True(filtro.EstaVazio);
        Assert.Empty(erro);
    }

    [Theory]
    [InlineData("Revisar", Decisao.Revisar)]
    [InlineData("bloquear", Decisao.Bloquear)]
    [InlineData("  PERMITIR  ", Decisao.Permitir)]
    public void Decisao_e_aceita_sem_depender_de_maiuscula_ou_espaco(string texto, Decisao esperada)
    {
        Assert.True(FiltroDeAlertas.TentarCriar(texto, null, null, null, null, null, out var filtro, out _));

        Assert.Equal(esperada, filtro.Decisao);
    }

    [Theory]
    [InlineData("Media", PrioridadeDeAlerta.Media)]
    [InlineData("alta", PrioridadeDeAlerta.Alta)]
    public void Prioridade_e_aceita_pelo_nome(string texto, PrioridadeDeAlerta esperada)
    {
        Assert.True(FiltroDeAlertas.TentarCriar(null, texto, null, null, null, null, out var filtro, out _));

        Assert.Equal(esperada, filtro.Prioridade);
    }

    [Theory]
    [InlineData("Inventada")]
    [InlineData("Aprovar")]
    [InlineData("'; DROP TABLE alertas; --")]
    [InlineData("2")]          // o numero subjacente de Revisar
    [InlineData("99")]         // valor que nao existe no enum
    [InlineData("-1")]
    public void Decisao_fora_do_vocabulario_e_recusada(string texto)
    {
        // O numero merece atencao especial: `Enum.TryParse` sozinho aceitaria
        // "2" como Revisar e deixaria "99" atravessar como um valor que nao
        // existe. Comparar com os NOMES fecha os dois buracos.
        Assert.False(FiltroDeAlertas.TentarCriar(texto, null, null, null, null, null, out var filtro, out var erro));

        Assert.True(filtro.EstaVazio);
        Assert.Contains("decisao", erro, StringComparison.Ordinal);

        // A mensagem lista o vocabulario aceito: e contrato publico, e nao ha
        // o que vazar em dizer que existem tres decisoes.
        Assert.Contains(nameof(Decisao.Bloquear), erro, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Critica")]
    [InlineData("1")]
    [InlineData("Baixa")]
    public void Prioridade_fora_do_vocabulario_e_recusada(string texto)
    {
        Assert.False(FiltroDeAlertas.TentarCriar(null, texto, null, null, null, null, out _, out var erro));

        Assert.Contains("prioridade", erro, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void Score_minimo_fora_da_faixa_do_produto_e_recusado(int score)
    {
        Assert.False(FiltroDeAlertas.TentarCriar(null, null, null, score, null, null, out _, out var erro));

        Assert.Contains("score", erro, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(100)]
    public void Score_minimo_nas_bordas_da_faixa_e_aceito(int score)
    {
        Assert.True(FiltroDeAlertas.TentarCriar(null, null, null, score, null, null, out var filtro, out _));

        Assert.Equal(score, filtro.ScoreMinimo);
    }

    [Fact]
    public void Periodo_invertido_e_recusado()
    {
        // Um periodo invertido devolveria zero alertas sem erro nenhum, e
        // quem consultou concluiria que nao ha nada para investigar.
        var inicio = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

        Assert.False(FiltroDeAlertas.TentarCriar(
            null,
            null,
            null,
            null,
            inicio,
            inicio.AddDays(-1),
            out _,
            out var erro));

        Assert.Contains("periodo", erro, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Periodo_com_inicio_igual_ao_fim_e_aceito()
    {
        var instante = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

        Assert.True(FiltroDeAlertas.TentarCriar(
            null,
            null,
            null,
            null,
            instante,
            instante,
            out var filtro,
            out _));

        Assert.Equal(instante, filtro.De);
        Assert.Equal(instante, filtro.Ate);
    }

    [Fact]
    public void Todos_os_filtros_juntos_sao_preservados()
    {
        var de = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var ate = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

        Assert.True(FiltroDeAlertas.TentarCriar("Bloquear", "Alta", null, 70, de, ate, out var filtro, out _));

        Assert.False(filtro.EstaVazio);
        Assert.Equal(Decisao.Bloquear, filtro.Decisao);
        Assert.Equal(PrioridadeDeAlerta.Alta, filtro.Prioridade);
        Assert.Equal(70, filtro.ScoreMinimo);
        Assert.Equal(de, filtro.De);
        Assert.Equal(ate, filtro.Ate);
    }

    [Theory]
    [InlineData("Aberto", StatusDoAlerta.Aberto)]
    [InlineData("emcaso", StatusDoAlerta.EmCaso)]
    [InlineData("Encerrado", StatusDoAlerta.Encerrado)]
    public void Status_do_alerta_e_aceito_pelo_nome(string texto, StatusDoAlerta esperado)
    {
        // O filtro de situacao e o que separa fila de trabalho de historico:
        // sem ele, um alerta ja investigado ficaria na fila para sempre.
        Assert.True(FiltroDeAlertas.TentarCriar(null, null, texto, null, null, null, out var filtro, out _));

        Assert.Equal(esperado, filtro.Status);
    }

    [Theory]
    [InlineData("Resolvido")]
    [InlineData("1")]
    [InlineData("Fechado")]
    public void Status_fora_do_vocabulario_e_recusado(string texto)
    {
        Assert.False(FiltroDeAlertas.TentarCriar(null, null, texto, null, null, null, out _, out var erro));

        Assert.Contains("status", erro, StringComparison.Ordinal);
    }

    [Fact]
    public void Uma_recusa_nao_deixa_filtro_pela_metade()
    {
        // Se a prioridade e invalida, a decisao valida que veio junto NAO pode
        // ser aplicada sozinha: o resultado seria uma lista que ninguem pediu.
        Assert.False(FiltroDeAlertas.TentarCriar("Bloquear", "Critica", null, null, null, null, out var filtro, out _));

        Assert.True(filtro.EstaVazio);
    }
}
