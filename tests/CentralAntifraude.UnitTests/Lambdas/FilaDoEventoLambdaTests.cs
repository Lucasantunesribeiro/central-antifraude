using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Infrastructure.Mensageria;

namespace CentralAntifraude.UnitTests.Lambdas;

/// <summary>
/// O adaptador entre o consumidor da Fase 5 e o modelo de ack do Lambda.
///
/// **O que estes testes protegem.** No SQS com gatilho de Lambda, a AWS apaga
/// tudo o que a invocação não reportar como falha. Um erro aqui não aparece
/// como exceção nem como fila crescendo: aparece como mensagem sumindo em
/// silêncio, que é o pior defeito possível numa fila.
/// </summary>
public sealed class FilaDoEventoLambdaTests
{
    private static readonly DateTimeOffset Instante =
        new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static MensagemRecebida Mensagem(string recibo) =>
        new(recibo, """{"eventId":"x"}""", 1, Instante);

    private static FilaDoEventoLambda Montar(params string[] recibos) =>
        new(new FilaInerte(), recibos.Select(Mensagem));

    [Fact]
    public async Task Mensagem_confirmada_nao_e_reportada_como_falha()
    {
        var fila = Montar("m-1");

        await fila.ReceberAsync(IFilaDeMensagens.FilaOperacional, 10, TestContext.Current.CancellationToken);
        await fila.ApagarAsync("m-1", TestContext.Current.CancellationToken);

        Assert.Empty(fila.RecibosComFalha);
    }

    /// <summary>
    /// O caso que motivou contar por subtração.
    ///
    /// Quando o consumidor falha por erro transitório ele não devolve a
    /// mensagem — deixa a visibilidade expirar, que é o comportamento certo
    /// quando a fila é uma tabela. Se a conta fosse pela soma das devoluções,
    /// esta mensagem sairia da invocação sem reporte, e a AWS a apagaria como
    /// se tivesse dado certo.
    /// </summary>
    [Fact]
    public async Task Mensagem_entregue_e_nunca_confirmada_e_reportada_como_falha()
    {
        var fila = Montar("m-1");

        await fila.ReceberAsync(IFilaDeMensagens.FilaOperacional, 10, TestContext.Current.CancellationToken);

        Assert.Equal(["m-1"], fila.RecibosComFalha);
    }

    [Fact]
    public async Task Mensagem_devolvida_e_reportada_como_falha()
    {
        var fila = Montar("m-1");

        await fila.ReceberAsync(IFilaDeMensagens.FilaOperacional, 10, TestContext.Current.CancellationToken);
        await fila.DevolverAsync("m-1", TestContext.Current.CancellationToken);

        Assert.Equal(["m-1"], fila.RecibosComFalha);
    }

    /// <summary>
    /// Falha parcial de verdade: uma ruim no meio de duas boas devolve uma só.
    ///
    /// Sem isto, a AWS reentregaria as três. A Inbox impediria o efeito
    /// duplicado, mas ao custo de três transações para descobrir que duas não
    /// tinham nada a fazer.
    /// </summary>
    [Fact]
    public async Task Apenas_a_mensagem_ruim_volta_para_a_fila()
    {
        var fila = Montar("m-1", "m-2", "m-3");

        await fila.ReceberAsync(IFilaDeMensagens.FilaOperacional, 10, TestContext.Current.CancellationToken);
        await fila.ApagarAsync("m-1", TestContext.Current.CancellationToken);
        await fila.DevolverAsync("m-2", TestContext.Current.CancellationToken);
        await fila.ApagarAsync("m-3", TestContext.Current.CancellationToken);

        Assert.Equal(["m-2"], fila.RecibosComFalha);
    }

    /// <summary>
    /// Nada entregue é nada a reportar — e não "tudo falhou".
    ///
    /// Importa porque o handler de backtests chama `ConsumirLoteAsync` em laço
    /// e só para quando o lote volta vazio. Se o não-entregue contasse como
    /// falha, uma invocação inteiramente bem-sucedida devolveria falhas.
    /// </summary>
    [Fact]
    public async Task Lote_vazio_nao_reporta_falha()
    {
        var fila = Montar();

        var lote = await fila.ReceberAsync(
            IFilaDeMensagens.FilaOperacional, 10, TestContext.Current.CancellationToken);

        Assert.Empty(lote);
        Assert.Empty(fila.RecibosComFalha);
    }

    /// <summary>
    /// A segunda leitura vem vazia: "a fila" de uma invocação é o que veio no
    /// evento. Sem isto, o laço do consumidor nunca terminaria.
    /// </summary>
    [Fact]
    public async Task Segunda_leitura_vem_vazia()
    {
        var fila = Montar("m-1");

        var primeira = await fila.ReceberAsync(
            IFilaDeMensagens.FilaOperacional, 10, TestContext.Current.CancellationToken);
        var segunda = await fila.ReceberAsync(
            IFilaDeMensagens.FilaOperacional, 10, TestContext.Current.CancellationToken);

        Assert.Single(primeira);
        Assert.Empty(segunda);
    }

    /// <summary>
    /// O limite de quantidade é respeitado, e o que ficou de fora NÃO conta
    /// como falha — ele volta na leitura seguinte, dentro da mesma invocação.
    /// É assim que o handler de backtests processa um lote maior que um.
    /// </summary>
    [Fact]
    public async Task Respeita_a_quantidade_pedida_e_entrega_o_resto_depois()
    {
        var fila = Montar("m-1", "m-2");

        var primeira = await fila.ReceberAsync(
            IFilaDeMensagens.FilaOperacional, 1, TestContext.Current.CancellationToken);

        Assert.Single(primeira);
        Assert.Equal(["m-1"], fila.RecibosComFalha);

        await fila.ApagarAsync("m-1", TestContext.Current.CancellationToken);

        var segunda = await fila.ReceberAsync(
            IFilaDeMensagens.FilaOperacional, 1, TestContext.Current.CancellationToken);

        Assert.Equal("m-2", Assert.Single(segunda).Recibo);
    }

    /// <summary>
    /// Enviar continua sendo a fila de verdade. Um efeito pode publicar um
    /// evento novo, e ele precisa sair para o SQS — não para o adaptador.
    /// </summary>
    [Fact]
    public async Task Enviar_delega_para_a_fila_real()
    {
        var real = new FilaInerte();
        var fila = new FilaDoEventoLambda(real, []);

        await fila.EnviarAsync(
            IFilaDeMensagens.FilaOperacional, "corpo", TestContext.Current.CancellationToken);

        Assert.Equal(1, real.Enviadas);
    }

    private sealed class FilaInerte : IFilaDeMensagens
    {
        public int Enviadas { get; private set; }

        public Task EnviarAsync(string fila, string corpo, CancellationToken cancellationToken)
        {
            Enviadas++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MensagemRecebida>> ReceberAsync(
            string fila, int quantidade, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ApagarAsync(string recibo, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DevolverAsync(string recibo, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> ContarPendentesAsync(string fila, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<int> ContarMortasAsync(string fila, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
