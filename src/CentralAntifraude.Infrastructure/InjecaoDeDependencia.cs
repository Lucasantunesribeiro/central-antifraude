using CentralAntifraude.Application.Alertas;
using CentralAntifraude.Application.Backtests;
using CentralAntifraude.Application.Auditoria;
using CentralAntifraude.Application.Operacao;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Correlacao;
using CentralAntifraude.Application.Eventos;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Application.Integracoes;
using CentralAntifraude.Application.Investigacao;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Application.Risco;
using CentralAntifraude.Application.Transacoes;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Tempo;
using CentralAntifraude.Infrastructure.Identidade;
using CentralAntifraude.Infrastructure.Integracoes;
using Amazon.Lambda;
using Amazon.SQS;
using CentralAntifraude.Infrastructure.Mensageria;
using CentralAntifraude.Infrastructure.Observabilidade;
using CentralAntifraude.Infrastructure.Persistencia;
using CentralAntifraude.Infrastructure.Persistencia.Repositorios;
using CentralAntifraude.Infrastructure.Tempo;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CentralAntifraude.Infrastructure;

/// <summary>
/// Ponto unico de registro da infraestrutura.
///
/// A API chama este metodo e nao precisa referenciar EF Core nem Npgsql -
/// e o que mantem a persistencia confinada a este projeto, condicao
/// verificada pelos testes de arquitetura.
/// </summary>
public static class InjecaoDeDependencia
{
    /// <summary>Nome do health check de banco exposto em /health/ready.</summary>
    public const string NomeDoHealthCheckDeBanco = "postgresql";

    /// <summary>Tag que separa dependencias externas do liveness do processo.</summary>
    public const string TagDeProntidao = "pronto";

    /// <summary>
    /// Iteracoes de PBKDF2-HMAC-SHA512 para senha.
    ///
    /// Valor recomendado pela OWASP Password Storage Cheat Sheet (consultada
    /// em 2026-09-03) para PBKDF2-HMAC-SHA512. O padrao da biblioteca sao
    /// 100.000; este numero e uma escolha do projeto com fonte, e nao um
    /// "padrao de mercado" inventado.
    /// </summary>
    public const int IteracoesDeHashDeSenha = 220_000;

    public static IServiceCollection AdicionarInfraestrutura(
        this IServiceCollection servicos,
        IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(servicos);
        ArgumentNullException.ThrowIfNull(configuracao);

        var stringDeConexao = configuracao.GetConnectionString(OpcoesDoDbContext.NomeDaConexao);

        // Falha fechada: sem banco configurado a aplicacao nao sobe pela
        // metade. Um processo no ar que responde 200 no liveness e falha em
        // toda avaliacao de risco e pior do que um processo que nao subiu.
        if (string.IsNullOrWhiteSpace(stringDeConexao))
        {
            throw new InvalidOperationException(
                $"String de conexao '{OpcoesDoDbContext.NomeDaConexao}' nao configurada. " +
                "Defina a variavel de ambiente " +
                $"ConnectionStrings__{OpcoesDoDbContext.NomeDaConexao} " +
                "ou use `dotnet user-secrets`. Ver docs/setup-local.md.");
        }

        servicos.AddDbContext<CentralAntifraudeDbContext>(
            opcoes => OpcoesDoDbContext.Configurar(opcoes, stringDeConexao));

        servicos.AddSingleton<IRelogio, RelogioSistema>();

        // Correlacao com escopo, registrada aqui e nao na Api, porque o escopo
        // nem sempre e uma requisicao: o consumidor de eventos tambem precisa
        // definir a correlacao que veio no envelope, para que o efeito que ele
        // produz minutos depois continue no mesmo fio (CLAUDE.md secao 69).
        servicos.AddScoped<ContextoDeCorrelacaoMutavel>();
        servicos.AddScoped<IContextoDeCorrelacao>(
            provedor => provedor.GetRequiredService<ContextoDeCorrelacaoMutavel>());

        AdicionarIdentidade(servicos, configuracao);
        AdicionarConcorrencia(servicos, configuracao);
        AdicionarRisco(servicos, configuracao);
        AdicionarBacktests(servicos, configuracao);
        AdicionarAlertas(servicos);
        AdicionarOperacao(servicos);
        AdicionarMensageria(servicos, configuracao);
        AdicionarIngestao(servicos, configuracao);

        servicos
            .AddHealthChecks()
            .AddDbContextCheck<CentralAntifraudeDbContext>(
                name: NomeDoHealthCheckDeBanco,
                tags: [TagDeProntidao]);

        return servicos;
    }

    /// <summary>
    /// Le e valida as opcoes de autenticacao.
    ///
    /// Publico porque a Api precisa dos mesmos valores para configurar a
    /// validacao do JWT. Chamar isto e melhor do que a Api montar um provedor
    /// de servicos paralelo so para espiar um singleton ja registrado.
    /// </summary>
    public static OpcoesDeAutenticacao LerOpcoesDeAutenticacao(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        var opcoes = new OpcoesDeAutenticacao();
        configuracao.GetSection(OpcoesDeAutenticacao.Secao).Bind(opcoes);

        // Falha fechada: configuracao de autenticacao invalida derruba a
        // inicializacao, em vez de virar "token invalido" em producao sem
        // ninguem entender por que.
        opcoes.Validar();

        return opcoes;
    }

    /// <summary>
    /// Le e valida as opcoes de ingestao. Publico pela mesma razao das opcoes
    /// de autenticacao: mais de um ponto da composicao precisa dos valores.
    /// </summary>
    public static OpcoesDeIngestao LerOpcoesDeIngestao(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        var opcoes = new OpcoesDeIngestao();
        configuracao.GetSection(OpcoesDeIngestao.Secao).Bind(opcoes);

        // Falha fechada: sem a chave de fingerprint a aplicacao nao sobe. O
        // contrario seria aceitar transacoes e gravar um fingerprint de IP
        // derivado de chave vazia - reversivel, e portanto inutil.
        opcoes.Validar();

        return opcoes;
    }

    /// <summary>
    /// Le e valida as opcoes de avaliacao de risco.
    /// </summary>
    public static OpcoesDeAvaliacao LerOpcoesDeAvaliacao(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        var opcoes = new OpcoesDeAvaliacao();
        configuracao.GetSection(OpcoesDeAvaliacao.Secao).Bind(opcoes);

        // Falha fechada: uma janela de historico menor que a maior janela de
        // regra faria a regra de velocidade ficar calada em vez de errar. Isso
        // nao pode passar despercebido ate producao.
        opcoes.Validar();

        return opcoes;
    }

    /// <summary>Le e valida as opcoes de backtest.</summary>
    public static OpcoesDeBacktest LerOpcoesDeBacktest(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        var opcoes = new OpcoesDeBacktest();
        configuracao.GetSection(OpcoesDeBacktest.Secao).Bind(opcoes);

        // Falha fechada: um teto de contexto menor do que o de transacoes
        // analisadas faria toda execucao no limite falhar por um motivo que a
        // mensagem nao explicaria. Melhor nao subir.
        opcoes.Validar();

        return opcoes;
    }

    /// <summary>Le e valida as opcoes de retry de concorrencia.</summary>
    public static OpcoesDeConcorrencia LerOpcoesDeConcorrencia(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        var opcoes = new OpcoesDeConcorrencia();
        configuracao.GetSection(OpcoesDeConcorrencia.Secao).Bind(opcoes);
        opcoes.Validar();

        return opcoes;
    }

    /// <summary>Le a configuracao de SQS. Ausencia significa "nao estamos na AWS".</summary>
    public static OpcoesDaFilaSqs LerOpcoesDaFilaSqs(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        var opcoes = new OpcoesDaFilaSqs();
        configuracao.GetSection(OpcoesDaFilaSqs.Secao).Bind(opcoes);

        return opcoes;
    }

    /// <summary>Le e valida as opcoes da fila.</summary>
    public static OpcoesDaFila LerOpcoesDaFila(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        var opcoes = new OpcoesDaFila();
        configuracao.GetSection(OpcoesDaFila.Secao).Bind(opcoes);
        opcoes.Validar();

        return opcoes;
    }

    /// <summary>Le e valida as opcoes dos lacos de fundo.</summary>
    public static OpcoesDeSegundoPlano LerOpcoesDeSegundoPlano(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        var opcoes = new OpcoesDeSegundoPlano();
        configuracao.GetSection(OpcoesDeSegundoPlano.Secao).Bind(opcoes);
        opcoes.Validar();

        return opcoes;
    }

    private static void AdicionarMensageria(IServiceCollection servicos, IConfiguration configuracao)
    {
        servicos.AddSingleton(LerOpcoesDaFila(configuracao));
        servicos.AddSingleton(LerOpcoesDeSegundoPlano(configuracao));

        // ---------------------------------------------------------------
        // Qual fila, e como o produto decide.
        //
        // O criterio e "existe uma URL de fila configurada?", e nao o nome do
        // ambiente. A pergunta que importa e literalmente essa — um nome de
        // ambiente e uma aproximacao que erra no dia em que alguem criar um
        // quarto ambiente.
        //
        // Fora da AWS continua valendo a FilaEmPostgres da Fase 5, que imita o
        // SQS Standard de proposito: entrega ao menos uma vez, sem ordem, com
        // visibilidade e redrive. E o que permite provar idempotencia e
        // concorrencia em teste, sem nuvem.
        // ---------------------------------------------------------------
        var opcoesDeSqs = LerOpcoesDaFilaSqs(configuracao);

        servicos.AddSingleton(opcoesDeSqs);

        if (opcoesDeSqs.UsaSqs)
        {
            servicos.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient());
            servicos.AddSingleton<IAmazonLambda>(_ => new AmazonLambdaClient());
            servicos.AddScoped<IFilaDeMensagens, FilaSqs>();
            servicos.AddScoped<IDespachanteImediato, DespachanteImediatoEmLambda>();
        }
        else
        {
            servicos.AddScoped<IFilaDeMensagens, FilaEmPostgres>();
            servicos.AddScoped<IDespachanteImediato, DespachanteImediatoInerte>();
        }
        servicos.AddScoped<DespachanteDeEventos>();
        servicos.AddScoped<ProcessadorDeEventos>();
        servicos.AddScoped<ProcessadorDeBacktests>();
        servicos.AddScoped<AmostradorDeIndicadores>();

        // Os efeitos de uma mensagem, na ordem em que serao aplicados.
        //
        // A ordem e estavel de proposito: ela decide o nome do savepoint de
        // cada um, e um teste que afirme "o alerta nao existe mas a projecao
        // sim" precisa de um comportamento reproduzivel. Acrescentar um efeito
        // e acrescentar uma linha aqui — e o novo consumidor comeca com a
        // Inbox vazia, entao trata os eventos que ainda estiverem na fila e
        // ignora os ja confirmados.
        servicos.AddScoped<IManipuladorDeEvento, ProjecaoDeDecisoesDiarias>();
        servicos.AddScoped<IManipuladorDeEvento, CriadorDeAlertas>();

        // Os lacos so fazem alguma coisa quando SegundoPlano:Habilitado esta
        // ligado. Registra-los sempre mantem uma unica composicao, em vez de
        // dois caminhos de inicializacao diferentes entre teste e producao.
        servicos.AddHostedService<LacoDoDespachante>();
        servicos.AddHostedService<LacoDoConsumidor>();
        servicos.AddHostedService<LacoDeBacktests>();
    }

    private static void AdicionarBacktests(IServiceCollection servicos, IConfiguration configuracao)
    {
        servicos.AddSingleton(LerOpcoesDeBacktest(configuracao));

        servicos.AddScoped<IRepositorioDeBacktests, RepositorioDeBacktests>();
        servicos.AddScoped<ServicoDeBacktests>();

        // O executor usa o mesmo MotorDeRisco singleton da avaliacao real. Se
        // um dia alguem registrar um motor diferente aqui, o ROADMAP 9.4 terá
        // sido violado nesta linha — e ha um teste de arquitetura que verifica
        // que o motor continua unico.
        servicos.AddScoped<ExecutorDeBacktest>();
    }

    /// <summary>
    /// Painel operacional, metricas de regra e leitura da trilha (Fase 10).
    ///
    /// A leitura da auditoria e registrada como interface PROPRIA, separada do
    /// registrador que so escreve. Uma interface unica convidaria, no futuro, a
    /// um metodo que altera — e uma trilha alteravel nao prova nada.
    /// </summary>
    private static void AdicionarOperacao(IServiceCollection servicos)
    {
        servicos.AddScoped<IRepositorioDeOperacao, RepositorioDeOperacao>();
        servicos.AddScoped<ServicoDoPainel>();

        servicos.AddScoped<IConsultaDeAuditoria, ConsultaDeAuditoriaEmPostgres>();
        servicos.AddScoped<ServicoDeAuditoria>();
    }

    private static void AdicionarAlertas(IServiceCollection servicos)
    {
        servicos.AddScoped<IRepositorioDeAlertas, RepositorioDeAlertas>();
        servicos.AddScoped<ServicoDeConsultaDeAlertas>();

        servicos.AddScoped<IRepositorioDeCasos, RepositorioDeCasos>();
        servicos.AddScoped<ServicoDeCasos>();
    }

    private static void AdicionarConcorrencia(IServiceCollection servicos, IConfiguration configuracao)
    {
        servicos.AddSingleton(LerOpcoesDeConcorrencia(configuracao));

        // Com escopo, e nao singleton: o executor abre transacao no
        // DbContext da requisicao. Um singleton aqui seria dependencia
        // cativa - ele guardaria para sempre o primeiro DbContext que
        // recebesse, e a segunda requisicao gravaria pelo contexto da
        // primeira.
        servicos.AddScoped<IExecutorDeOperacaoCritica, ExecutorDeOperacaoCritica>();
        servicos.AddScoped<IRepositorioDeEventos, RepositorioDeEventos>();
    }

    private static void AdicionarRisco(IServiceCollection servicos, IConfiguration configuracao)
    {
        servicos.AddSingleton(LerOpcoesDeAvaliacao(configuracao));

        // O motor nao tem estado nem dependencia com escopo: e uma funcao pura
        // de (transacao, contexto, versao do perfil) para avaliacao. Singleton
        // deixa isso explicito - se um dia alguem tentar injetar um DbContext
        // nele, o container reclama na inicializacao.
        servicos.AddSingleton<MotorDeRisco>();

        servicos.AddScoped<IRepositorioDeRisco, RepositorioDeRisco>();
        servicos.AddScoped<IProvedorDeContextoDeRisco, ProvedorDeContextoDeRisco>();

        servicos.AddScoped<ServicoDeAvaliacaoDeRisco>();
        servicos.AddScoped<ServicoDeConsultaDeRisco>();
        servicos.AddScoped<ServicoDeGestaoDeRegras>();
    }

    private static void AdicionarIngestao(IServiceCollection servicos, IConfiguration configuracao)
    {
        servicos.AddSingleton(LerOpcoesDeIngestao(configuracao));

        servicos.AddSingleton<IProtetorDeCredencial, ProtetorDeCredencial>();
        servicos.AddSingleton<IFingerprintDeIp, FingerprintDeIpComHmac>();

        servicos.AddScoped<IRepositorioDeIntegracoes, RepositorioDeIntegracoes>();
        servicos.AddScoped<IRepositorioDeTransacoes, RepositorioDeTransacoes>();

        servicos.AddScoped<ServicoDeIntegracoes>();
        servicos.AddScoped<ServicoDeIngestao>();
    }

    private static void AdicionarIdentidade(IServiceCollection servicos, IConfiguration configuracao)
    {
        var opcoes = LerOpcoesDeAutenticacao(configuracao);

        servicos.AddSingleton(opcoes);

        servicos.Configure<PasswordHasherOptions>(
            o => o.IterationCount = IteracoesDeHashDeSenha);

        servicos.AddSingleton<IHashDeSenha, HashDeSenhaPbkdf2>();
        servicos.AddSingleton<IProtetorDeRefreshToken, ProtetorDeRefreshToken>();
        servicos.AddSingleton<IEmissorDeAccessToken, EmissorDeAccessToken>();

        servicos.AddScoped<IRepositorioDeUsuarios, RepositorioDeUsuarios>();
        servicos.AddScoped<IRepositorioDeOrganizacoes, RepositorioDeOrganizacoes>();
        servicos.AddScoped<IRepositorioDeRefreshTokens, RepositorioDeRefreshTokens>();
        servicos.AddScoped<IRegistradorDeAuditoria, RegistradorDeAuditoria>();
        servicos.AddScoped<IUnidadeDeTrabalho, UnidadeDeTrabalho>();

        servicos.AddScoped<ServicoDeAutenticacao>();
        servicos.AddScoped<ServicoDeUsuarios>();
    }
}
