using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// O contrato da fila, verificado contra a implementacao local.
///
/// **Este arquivo e o que torna a Fase 14 uma troca, e nao uma reescrita.**
/// Cada teste aqui descreve um comportamento do SQS Standard, sem citar
/// PostgreSQL em lugar nenhum. Quando o adaptador do SDK existir, ele passa a
/// responder a estes mesmos testes — e o que passar continua valendo.
///
/// A tentacao seria testar "a mensagem chega". O que precisa ser testado e o
/// contrario: que a fila **nao** promete entrega unica, **nao** promete ordem,
/// e **esconde** em vez de remover. Sao essas tres coisas que o codigo do
/// consumidor precisa suportar, e uma fila boazinha demais esconderia o
/// defeito ate a producao.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class FilaDeMensagensTests : IAsyncLifetime
{
    private const string Fila = IFilaDeMensagens.FilaOperacional;

    private readonly FixtureDoBanco _banco;
    private CenarioDeMensageria _cenario = null!;

    public FilaDeMensagensTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        // Cada teste comeca com a fila limpa. As filas nao tem tenant — sao a
        // simulacao de um servico externo —, entao a limpeza e global.
        await CenarioDeMensageria.LimparAsync(_banco.StringDeConexao, Cancelamento);

        _cenario = CenarioDeMensageria.Criar(_banco.StringDeConexao);
    }

    public ValueTask DisposeAsync() => _cenario.DisposeAsync();

    // -----------------------------------------------------------------------
    // Visibilidade
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Receber_esconde_a_mensagem_em_vez_de_remove_la()
    {
        // A diferenca decide o que acontece quando o consumidor morre no meio.
        // Se receber removesse, a queda viraria perda; escondendo, a mensagem
        // volta sozinha quando a visibilidade expira.
        await _cenario.Fila.EnviarAsync(Fila, Corpo("um"), Cancelamento);

        var recebidas = await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento);

        Assert.Single(recebidas);
        Assert.Equal(1, await _cenario.Fila.ContarPendentesAsync(Fila, Cancelamento));

        // Escondida: um segundo recebimento nao a enxerga.
        Assert.Empty(await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento));
    }

    [Fact]
    public async Task Apagar_e_o_que_remove_de_verdade()
    {
        await _cenario.Fila.EnviarAsync(Fila, Corpo("um"), Cancelamento);

        var mensagem = Assert.Single(await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento));

        await _cenario.Fila.ApagarAsync(mensagem.Recibo, Cancelamento);

        Assert.Equal(0, await _cenario.Fila.ContarPendentesAsync(Fila, Cancelamento));
    }

    [Fact]
    public async Task Devolver_traz_a_mensagem_de_volta_na_hora_e_conta_a_entrega()
    {
        await _cenario.Fila.EnviarAsync(Fila, Corpo("um"), Cancelamento);

        var primeira = Assert.Single(await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento));
        Assert.Equal(1, primeira.RecebimentosAproximados);

        await _cenario.Fila.DevolverAsync(primeira.Recibo, Cancelamento);

        var segunda = Assert.Single(await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento));

        // A contagem de entregas cresce. E ela que a politica de redrive usa
        // para decidir que uma mensagem nao vai dar certo nunca.
        Assert.Equal(2, segunda.RecebimentosAproximados);
    }

    [Fact]
    public async Task Recibo_antigo_nao_apaga_a_entrega_seguinte()
    {
        // Cenario real: a visibilidade expira porque o processamento demorou,
        // outro consumidor recebe a mesma mensagem, e so entao o primeiro
        // termina e tenta confirmar. Se o recibo velho ainda valesse, ele
        // apagaria uma mensagem que outro consumidor esta processando agora.
        await _cenario.Fila.EnviarAsync(Fila, Corpo("um"), Cancelamento);

        var primeira = Assert.Single(await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento));

        await _cenario.Fila.DevolverAsync(primeira.Recibo, Cancelamento);

        var segunda = Assert.Single(await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento));

        await _cenario.Fila.ApagarAsync(primeira.Recibo, Cancelamento);

        // A mensagem continua la, com o recibo novo valendo.
        Assert.Equal(1, await _cenario.Fila.ContarPendentesAsync(Fila, Cancelamento));

        await _cenario.Fila.ApagarAsync(segunda.Recibo, Cancelamento);

        Assert.Equal(0, await _cenario.Fila.ContarPendentesAsync(Fila, Cancelamento));
    }

    // -----------------------------------------------------------------------
    // Lote e concorrencia
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Receber_respeita_o_tamanho_do_lote()
    {
        for (var i = 0; i < 7; i++)
        {
            await _cenario.Fila.EnviarAsync(Fila, Corpo($"m{i}"), Cancelamento);
        }

        var lote = await _cenario.Fila.ReceberAsync(Fila, 3, Cancelamento);

        Assert.Equal(3, lote.Count);
    }

    [Fact]
    public async Task Dois_consumidores_nunca_recebem_a_mesma_mensagem()
    {
        // A garantia que permite escalar o worker. Cada consumidor precisa do
        // proprio contexto: e a situacao real, dois processos.
        for (var i = 0; i < 20; i++)
        {
            await _cenario.Fila.EnviarAsync(Fila, Corpo($"m{i}"), Cancelamento);
        }

        await using var outro = CenarioDeMensageria.Criar(_banco.StringDeConexao);

        var lotes = await Task.WhenAll(
            _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento),
            outro.Fila.ReceberAsync(Fila, 10, Cancelamento));

        var recibos = lotes.SelectMany(l => l.Select(m => m.Recibo)).ToList();

        Assert.Equal(20, recibos.Count);
        Assert.Equal(20, recibos.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Filas_diferentes_nao_se_misturam()
    {
        // Backtest e evento operacional nao podem dividir fila (CLAUDE.md
        // secao 30): um backtest longo ficaria na frente de um alerta.
        await _cenario.Fila.EnviarAsync(Fila, Corpo("operacional"), Cancelamento);
        await _cenario.Fila.EnviarAsync(IFilaDeMensagens.FilaDeBacktests, Corpo("backtest"), Cancelamento);

        var operacional = Assert.Single(await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento));

        Assert.Contains("operacional", operacional.Corpo, StringComparison.Ordinal);
        Assert.Equal(1, await _cenario.Fila.ContarPendentesAsync(IFilaDeMensagens.FilaDeBacktests, Cancelamento));
    }

    // -----------------------------------------------------------------------
    // Redrive
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Mensagem_que_nunca_e_confirmada_vai_para_a_fila_de_mortas()
    {
        // Sem redrive, uma mensagem defeituosa circula para sempre: consome o
        // worker a cada ciclo e nunca sai. A DLQ e o que a tira de circulacao
        // sem apaga-la — porque ela e a evidencia do defeito.
        await _cenario.Fila.EnviarAsync(Fila, Corpo("veneno"), Cancelamento);

        for (var i = 0; i < _cenario.Opcoes.MaximoDeRecebimentos; i++)
        {
            var recebida = await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento);

            Assert.Single(recebida);

            await _cenario.Fila.DevolverAsync(recebida[0].Recibo, Cancelamento);
        }

        // Na entrega seguinte ela ja passou do limite: sai de circulacao.
        Assert.Empty(await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento));

        Assert.Equal(0, await _cenario.Fila.ContarPendentesAsync(Fila, Cancelamento));
        Assert.Equal(1, await _cenario.Fila.ContarMortasAsync(Fila, Cancelamento));
    }

    [Fact]
    public async Task Mensagem_morta_guarda_o_corpo_e_o_motivo()
    {
        // A DLQ existe para investigacao (CLAUDE.md secao 72). Uma DLQ que
        // guardasse so o identificador nao responderia "o que essa mensagem
        // tinha de errado?".
        await _cenario.Fila.EnviarAsync(Fila, Corpo("veneno"), Cancelamento);

        for (var i = 0; i < _cenario.Opcoes.MaximoDeRecebimentos; i++)
        {
            var recebida = await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento);
            await _cenario.Fila.DevolverAsync(recebida[0].Recibo, Cancelamento);
        }

        await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento);

        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        var morta = Assert.Single(await contexto.MensagensMortas.ToListAsync(Cancelamento));

        Assert.Equal(Fila, morta.Fila);
        Assert.Contains("veneno", morta.Corpo, StringComparison.Ordinal);
        Assert.Equal(_cenario.Opcoes.MaximoDeRecebimentos, morta.Recebimentos);
        Assert.NotEmpty(morta.Motivo);
    }

    [Fact]
    public async Task Mensagem_boa_ao_lado_de_uma_envenenada_nao_e_afetada()
    {
        // Isolamento entre mensagens. Uma mensagem que vai para a DLQ nao pode
        // levar as vizinhas junto nem impedir que elas sejam processadas.
        await _cenario.Fila.EnviarAsync(Fila, Corpo("veneno"), Cancelamento);
        await _cenario.Fila.EnviarAsync(Fila, Corpo("boa"), Cancelamento);

        for (var i = 0; i < _cenario.Opcoes.MaximoDeRecebimentos; i++)
        {
            var lote = await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento);

            foreach (var mensagem in lote)
            {
                if (mensagem.Corpo.Contains("veneno", StringComparison.Ordinal))
                {
                    await _cenario.Fila.DevolverAsync(mensagem.Recibo, Cancelamento);
                }
                else
                {
                    await _cenario.Fila.ApagarAsync(mensagem.Recibo, Cancelamento);
                }
            }
        }

        await _cenario.Fila.ReceberAsync(Fila, 10, Cancelamento);

        Assert.Equal(0, await _cenario.Fila.ContarPendentesAsync(Fila, Cancelamento));
        Assert.Equal(1, await _cenario.Fila.ContarMortasAsync(Fila, Cancelamento));
    }

    private static string Corpo(string marca) => $$"""{"marca":"{{marca}}"}""";
}
