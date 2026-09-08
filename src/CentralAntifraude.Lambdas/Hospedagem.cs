using CentralAntifraude.Infrastructure;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Infrastructure.Configuracao;
using CentralAntifraude.Infrastructure.Identidade;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CentralAntifraude.Lambdas;

/// <summary>
/// O provedor de servicos que os tres handlers compartilham.
///
/// **Estatico de propósito, e é a otimização que mais importa aqui.** O Lambda
/// reaproveita o ambiente de execução entre invocações; tudo o que estiver num
/// campo estático sobrevive de uma para a outra. Montar o contêiner de injeção
/// a cada invocação significaria reconstruir o pool de conexões, reler a
/// configuração e reprovisionar o EF Core toda vez — o que transformaria cada
/// invocação num arranque frio.
///
/// A montagem acontece na primeira chamada, e o `Lazy` garante que duas
/// invocações simultâneas na mesma instância não a façam duas vezes.
///
/// **A composição é a MESMA da API.** `AdicionarInfraestrutura` é a única porta
/// de registro do produto desde a Fase 0, e usá-la aqui é o que impede os
/// workers de divergirem silenciosamente do que a API faz — regra de retry,
/// filtro de tenant, política de alertas, tudo vem junto.
/// </summary>
internal static class Hospedagem
{
    private static readonly Lazy<ServiceProvider> Provedor = new(Montar);

    public static IServiceScope AbrirEscopo() => Provedor.Value.CreateScope();

    /// <summary>
    /// A montagem, separada do `Lazy` estático para que um teste possa
    /// exercitá-la com configuração própria.
    ///
    /// Sem esta separação, a única forma de descobrir que falta um registro é
    /// invocar a função na AWS — foi assim que a ausência de
    /// `IContextoDoUsuarioAtual` chegou a produção.
    /// </summary>
    internal static ServiceProvider MontarCom(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        var servicos = new ServiceCollection();

        servicos.AddLogging(log =>
        {
            log.ClearProviders();
            log.AddJsonConsole(opcoes => opcoes.IncludeScopes = true);
            log.SetMinimumLevel(LogLevel.Information);
        });

        // **Sem este registro nada funciona, e o erro só aparece na invocação.**
        //
        // O `CentralAntifraudeDbContext` exige `IContextoDoUsuarioAtual` para o
        // filtro global de tenant. Na API quem o fornece é a camada web, a
        // partir do token da requisição — e num worker não há requisição.
        //
        // O contexto anônimo é o correto AQUI, e não uma gambiarra: despachante
        // e consumidor atravessam tenants por natureza, e ambos já desligam o
        // filtro pelo nome nas consultas em que isso importa. Um contexto com
        // organização fixa é que seria errado — escolheria um tenant.
        servicos.AddScoped<IContextoDoUsuarioAtual>(_ => ContextoDeUsuarioFixo.Anonimo);

        servicos.AdicionarInfraestrutura(configuracao);

        return servicos.BuildServiceProvider(validateScopes: true);
    }

    private static ServiceProvider Montar()
    {
        // Num Lambda não há appsettings: a configuração chega por variável de
        // ambiente (o que o template declara) e pelo Parameter Store (o que é
        // segredo). A ordem importa — o SSM entra depois, e portanto vence,
        // que é o que se quer quando um segredo é rotacionado.
        var construtor = new ConfigurationBuilder().AddEnvironmentVariables();

        var configuracao = construtor
            .AdicionarParametrosDaNuvem(
                Environment.GetEnvironmentVariable("Ambiente") ?? "producao")
            .Build();

        // JSON no console, como na API: é o formato que o CloudWatch indexa
        // por propriedade, e é o que faz o `CorrelationId` da Fase 12 virar
        // campo consultável em vez de texto dentro da mensagem.
        return MontarCom(configuracao);
    }
}
