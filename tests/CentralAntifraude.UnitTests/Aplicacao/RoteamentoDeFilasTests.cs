using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Domain.Eventos;

namespace CentralAntifraude.UnitTests.Aplicacao;

/// <summary>
/// Qual fila recebe cada evento.
///
/// Duas filas existem por uma razao operacional (CLAUDE.md secao 30): um
/// backtest de milhares de transacoes na mesma fila do alerta faria o analista
/// esperar sem que nada no sistema explicasse por que.
/// </summary>
public class RoteamentoDeFilasTests
{
    [Fact]
    public void Backtest_vai_para_a_fila_dedicada()
    {
        Assert.Equal(
            IFilaDeMensagens.FilaDeBacktests,
            RoteamentoDeFilas.Para(BacktestSolicitadoV1.NomeDoTipo));
    }

    [Fact]
    public void Transacao_avaliada_continua_na_fila_operacional()
    {
        Assert.Equal(
            IFilaDeMensagens.FilaOperacional,
            RoteamentoDeFilas.Para(TransacaoAvaliadaV1.NomeDoTipo));
    }

    [Fact]
    public void As_duas_filas_sao_mesmo_diferentes()
    {
        // Parece obvio, e e exatamente o tipo de constante que alguem alinha
        // por engano num refactor. Se as duas apontarem para o mesmo nome, a
        // separacao vira decoracao.
        Assert.NotEqual(IFilaDeMensagens.FilaOperacional, IFilaDeMensagens.FilaDeBacktests);
    }

    [Fact]
    public void Tipo_desconhecido_cai_na_fila_operacional()
    {
        // Escolha conservadora: a fila operacional tem consumidor, e o
        // consumidor recusa o que nao entende e manda para a DLQ. Cair na fila
        // de backtests deixaria a mensagem sem ninguem para recusa-la de forma
        // significativa.
        Assert.Equal(
            IFilaDeMensagens.FilaOperacional,
            RoteamentoDeFilas.Para("AlgoQueNinguemPublica.v9"));
    }

    [Fact]
    public void Tipo_vazio_falha_alto()
    {
        // Rotear "" para a fila operacional entregaria uma mensagem sem tipo a
        // um consumidor que espera um. Melhor quebrar no despacho.
        Assert.Throws<ArgumentException>(() => RoteamentoDeFilas.Para(" "));
    }
}
