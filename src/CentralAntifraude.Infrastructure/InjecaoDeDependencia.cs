using CentralAntifraude.Application.Auditoria;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Eventos;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Application.Integracoes;
using CentralAntifraude.Application.Risco;
using CentralAntifraude.Application.Transacoes;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Tempo;
using CentralAntifraude.Infrastructure.Identidade;
using CentralAntifraude.Infrastructure.Integracoes;
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

        AdicionarIdentidade(servicos, configuracao);
        AdicionarConcorrencia(servicos, configuracao);
        AdicionarRisco(servicos, configuracao);
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

    /// <summary>Le e valida as opcoes de retry de concorrencia.</summary>
    public static OpcoesDeConcorrencia LerOpcoesDeConcorrencia(IConfiguration configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        var opcoes = new OpcoesDeConcorrencia();
        configuracao.GetSection(OpcoesDeConcorrencia.Secao).Bind(opcoes);
        opcoes.Validar();

        return opcoes;
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
