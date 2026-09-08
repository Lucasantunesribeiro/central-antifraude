using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.Runtime;
using CentralAntifraude.Infrastructure.Mensageria;
using Microsoft.Extensions.Logging.Abstractions;

namespace CentralAntifraude.UnitTests.Lambdas;

/// <summary>
/// O aviso que a API dá ao despachante logo depois do commit.
///
/// **Ele é otimização, e não garantia.** A garantia continua sendo a Outbox
/// mais a varredura de quinze minutos. É essa distinção que estes testes
/// prendem: se um dia alguém fizer a falha subir daqui, a transação passaria a
/// morrer por causa de um aviso — depois de já ter sido confirmada.
/// </summary>
public sealed class DespachanteImediatoTests
{
    [Fact]
    public async Task Fora_da_AWS_acordar_nao_faz_nada()
    {
        var despachante = new DespachanteImediatoInerte();

        await despachante.AcordarAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A invocação é assíncrona, e não síncrona.
    ///
    /// Com `RequestResponse`, a resposta ao integrador ficaria presa esperando
    /// o despacho terminar — e o despacho acorda o banco e publica um lote. O
    /// integrador já tem o que pediu: a decisão de risco.
    /// </summary>
    [Fact]
    public async Task Invoca_a_funcao_sem_esperar_pela_resposta()
    {
        var lambda = new LambdaEspiao();
        var despachante = new DespachanteImediatoEmLambda(
            lambda,
            new OpcoesDaFilaSqs { FuncaoDoDespachante = "despachante-demo" },
            NullLogger<DespachanteImediatoEmLambda>.Instance);

        await despachante.AcordarAsync(TestContext.Current.CancellationToken);

        var pedido = Assert.Single(lambda.Pedidos);
        Assert.Equal("despachante-demo", pedido.FunctionName);
        Assert.Equal(InvocationType.Event, pedido.InvocationType);
    }

    /// <summary>
    /// Sem nome de função configurado, nem tenta.
    ///
    /// É o que impede a API de gastar uma chamada à AWS por transação num
    /// ambiente onde não existe função nenhuma para acordar.
    /// </summary>
    [Fact]
    public async Task Sem_funcao_configurada_nao_chama_a_AWS()
    {
        var lambda = new LambdaEspiao();
        var despachante = new DespachanteImediatoEmLambda(
            lambda,
            new OpcoesDaFilaSqs(),
            NullLogger<DespachanteImediatoEmLambda>.Instance);

        await despachante.AcordarAsync(TestContext.Current.CancellationToken);

        Assert.Empty(lambda.Pedidos);
    }

    /// <summary>
    /// **O teste mais importante deste arquivo.** A AWS fora do ar não pode
    /// derrubar uma transação que já foi confirmada — o integrador receberia
    /// um erro por um evento que a varredura publicaria sozinha minutos depois,
    /// e reenviaria a mesma transação para receber o mesmo erro.
    /// </summary>
    [Fact]
    public async Task Falha_da_AWS_nao_sobe_para_quem_chamou()
    {
        var lambda = new LambdaEspiao { Erro = new AmazonLambdaException("indisponivel") };
        var despachante = new DespachanteImediatoEmLambda(
            lambda,
            new OpcoesDaFilaSqs { FuncaoDoDespachante = "despachante-demo" },
            NullLogger<DespachanteImediatoEmLambda>.Instance);

        await despachante.AcordarAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Cancelamento continua subindo. Não é falha da AWS: é o processo sendo
    /// encerrado, e engolir isso esconderia um desligamento em andamento.
    /// </summary>
    [Fact]
    public async Task Cancelamento_continua_subindo()
    {
        var lambda = new LambdaEspiao { Erro = new OperationCanceledException() };
        var despachante = new DespachanteImediatoEmLambda(
            lambda,
            new OpcoesDaFilaSqs { FuncaoDoDespachante = "despachante-demo" },
            NullLogger<DespachanteImediatoEmLambda>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => despachante.AcordarAsync(TestContext.Current.CancellationToken));
    }

    private sealed class LambdaEspiao : AmazonLambdaClient
    {
        public LambdaEspiao()
            : base(
                new BasicAWSCredentials("chave-de-teste", "segredo-de-teste"),
                new AmazonLambdaConfig { ServiceURL = "http://localhost:1", MaxErrorRetry = 0 })
        {
        }

        public List<InvokeRequest> Pedidos { get; } = [];

        public Exception? Erro { get; init; }

        public override Task<InvokeResponse> InvokeAsync(
            InvokeRequest request,
            CancellationToken cancellationToken = default)
        {
            Pedidos.Add(request);

            return Erro is null
                ? Task.FromResult(new InvokeResponse { StatusCode = 202 })
                : Task.FromException<InvokeResponse>(Erro);
        }
    }
}
