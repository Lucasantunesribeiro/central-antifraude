using CentralAntifraude.Application.Backtests;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Infrastructure.Mensageria;
using CentralAntifraude.Infrastructure.Observabilidade;
using CentralAntifraude.Lambdas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CentralAntifraude.UnitTests.Lambdas;

/// <summary>
/// O contêiner de injeção que os três handlers compartilham.
///
/// **Este arquivo existe por causa de um defeito que chegou a produção.** A
/// stack subiu, o CloudFormation ficou verde, os health checks passaram — e
/// toda invocação do despachante morria com "Unable to resolve service for type
/// 'IContextoDoUsuarioAtual' while attempting to activate
/// 'CentralAntifraudeDbContext'".
///
/// A causa era simples e invisível: o `DbContext` exige `IContextoDoUsuarioAtual`
/// para o filtro global de tenant, e quem o registra é a camada web, a partir do
/// token da requisição. Num worker não há requisição, e `AdicionarInfraestrutura`
/// sozinho não preenche essa lacuna.
///
/// **Por que nenhum teste anterior pegou.** Os testes de integração montam a
/// aplicação pelo `WebApplicationFactory`, que passa pelo `Program.cs` da API — e
/// é lá que o registro existe. O contêiner dos Lambdas é outro grafo, e ninguém
/// o montava fora da AWS.
///
/// Estes testes montam esse grafo e pedem exatamente o que cada handler pede. Um
/// registro faltando passa a falhar aqui, em milissegundos, em vez de aparecer
/// como função morta depois do deploy.
/// </summary>
public sealed class HospedagemDosLambdasTests
{
    /// <summary>
    /// Configuração de mentira, mas com a FORMA da verdadeira.
    ///
    /// Não conecta em nada: registrar um `DbContext` não abre conexão, e é essa
    /// separação que permite conferir o grafo inteiro sem banco.
    /// </summary>
    private static IConfiguration Configuracao() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] =
                    "Host=localhost;Port=5432;Database=x;Username=x;Password=x",
                ["Autenticacao:ChaveDeAssinatura"] = new string('k', 64),
                ["Ingestao:ChaveDeFingerprint"] = new string('f', 44),
                // Com estas duas, a composição escolhe o caminho de PRODUÇÃO —
                // SQS e despachante em Lambda — e não o de desenvolvimento.
                // Testar o grafo de desenvolvimento não provaria nada sobre o
                // que roda na AWS.
                ["Mensageria:FilaOperacional"] =
                    "https://sqs.us-east-1.amazonaws.com/000000000000/fila",
                ["Mensageria:FilaDeBacktests"] =
                    "https://sqs.us-east-1.amazonaws.com/000000000000/backtests",
                ["Mensageria:FuncaoDoDespachante"] = "despachante",
                // A regiao, como a Lambda a forneceria por AWS_REGION. Sem ela,
                // construir o cliente AWS falha com "No RegionEndpoint
                // configured" — e foi exatamente esse o defeito que passou na
                // maquina de dev (que tinha a variavel no ambiente) e quebrou o
                // CI num runner limpo.
                ["AWS:Region"] = "us-east-1",
            })
            .Build();

    /// <summary>
    /// O que o <c>DespachanteHandler</c> resolve, na ordem em que ele resolve.
    /// </summary>
    [Fact]
    public void O_grafo_do_despachante_esta_completo()
    {
        using var provedor = Hospedagem.MontarCom(Configuracao());
        using var escopo = provedor.CreateScope();

        Assert.NotNull(escopo.ServiceProvider.GetRequiredService<DespachanteDeEventos>());
        Assert.NotNull(escopo.ServiceProvider.GetRequiredService<AmostradorDeIndicadores>());
    }

    /// <summary>
    /// O <c>ConsumidorHandler</c> monta o processador com
    /// <c>ActivatorUtilities</c>, injetando a fila do evento e resolvendo o
    /// resto do contêiner. É essa construção que o teste repete.
    /// </summary>
    [Fact]
    public void O_grafo_do_consumidor_esta_completo()
    {
        using var provedor = Hospedagem.MontarCom(Configuracao());
        using var escopo = provedor.CreateScope();

        var fila = new FilaDoEventoLambda(
            escopo.ServiceProvider.GetRequiredService<IFilaDeMensagens>(),
            []);

        var processador = ActivatorUtilities.CreateInstance<ProcessadorDeEventos>(
            escopo.ServiceProvider,
            fila);

        Assert.NotNull(processador);
    }

    [Fact]
    public void O_grafo_dos_backtests_esta_completo()
    {
        using var provedor = Hospedagem.MontarCom(Configuracao());
        using var escopo = provedor.CreateScope();

        var fila = new FilaDoEventoLambda(
            escopo.ServiceProvider.GetRequiredService<IFilaDeMensagens>(),
            []);

        var processador = ActivatorUtilities.CreateInstance<ProcessadorDeBacktests>(
            escopo.ServiceProvider,
            fila);

        Assert.NotNull(processador);
        Assert.NotNull(escopo.ServiceProvider.GetRequiredService<ExecutorDeBacktest>());
    }

    /// <summary>
    /// Com fila configurada, a composição entrega SQS — e não a fila em tabela.
    ///
    /// É a variável que decide qual implementação a injeção escolhe. Se ela
    /// escolhesse errado, o worker rodaria sem erro nenhum, falando com a fila
    /// errada, e a única evidência seria um alerta que nunca chega.
    /// </summary>
    [Fact]
    public void Com_fila_configurada_a_composicao_escolhe_o_SQS()
    {
        using var provedor = Hospedagem.MontarCom(Configuracao());
        using var escopo = provedor.CreateScope();

        Assert.IsType<FilaSqs>(escopo.ServiceProvider.GetRequiredService<IFilaDeMensagens>());
        Assert.IsType<DespachanteImediatoEmLambda>(
            escopo.ServiceProvider.GetRequiredService<IDespachanteImediato>());
    }

    /// <summary>
    /// O contêiner é construído com validação de escopo ligada.
    ///
    /// Sem ela, um serviço singleton que dependesse de um scoped passaria na
    /// montagem e explodiria na segunda invocação — uma dependência cativa, que
    /// é o defeito mais difícil de reproduzir num ambiente serverless, porque
    /// depende de a instância ser reaproveitada.
    /// </summary>
    [Fact]
    public void Resolver_o_grafo_duas_vezes_seguidas_continua_funcionando()
    {
        using var provedor = Hospedagem.MontarCom(Configuracao());

        for (var i = 0; i < 2; i++)
        {
            using var escopo = provedor.CreateScope();
            Assert.NotNull(escopo.ServiceProvider.GetRequiredService<DespachanteDeEventos>());
        }
    }
}
