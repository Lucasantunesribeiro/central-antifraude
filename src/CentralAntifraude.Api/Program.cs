using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CentralAntifraude.Api.Alertas;
using CentralAntifraude.Api.Backtests;
using CentralAntifraude.Api.Correlacao;
using CentralAntifraude.Api.Diagnostico;
using CentralAntifraude.Api.Erros;
using CentralAntifraude.Api.Identidade;
using CentralAntifraude.Api.Integracoes;
using CentralAntifraude.Api.Investigacao;
using CentralAntifraude.Api.Risco;
using CentralAntifraude.Application.Correlacao;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Application.Integracoes;
using CentralAntifraude.Infrastructure;
using CentralAntifraude.Infrastructure.Identidade;
using CentralAntifraude.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;

var construtor = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Logs estruturados (CLAUDE.md secao 70).
// Console em JSON fora de Development: e o formato que CloudWatch e qualquer
// coletor conseguem indexar por propriedade, sem parser de texto.
// ---------------------------------------------------------------------------
construtor.Logging.ClearProviders();
if (construtor.Environment.IsDevelopment())
{
    construtor.Logging.AddSimpleConsole(opcoes =>
    {
        opcoes.SingleLine = true;
        opcoes.IncludeScopes = true;
        opcoes.TimestampFormat = "HH:mm:ss ";
    });
}
else
{
    construtor.Logging.AddJsonConsole(opcoes => opcoes.IncludeScopes = true);
}

// ---------------------------------------------------------------------------
// Contrato JSON (CLAUDE.md secoes 53 e 54).
// ---------------------------------------------------------------------------
construtor.Services.ConfigureHttpJsonOptions(opcoes =>
{
    var serializacao = opcoes.SerializerOptions;

    serializacao.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;

    // Estrito de proposito: um payload com campo desconhecido e recusado em
    // vez de aceito pela metade. Um integrador que escreve "amout" no lugar de
    // "amount" precisa descobrir isso na primeira chamada, nao depois de mil
    // transacoes avaliadas sem o valor.
    serializacao.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    serializacao.PropertyNameCaseInsensitive = false;
    serializacao.AllowTrailingCommas = false;
    serializacao.ReadCommentHandling = JsonCommentHandling.Disallow;
    serializacao.NumberHandling = JsonNumberHandling.Strict;

    // Enums viajam como texto: "Revisar" e um contrato estavel e legivel;
    // o inteiro 1 muda de significado se alguem reordenar o enum.
    serializacao.Converters.Add(new JsonStringEnumConverter());
});

// ---------------------------------------------------------------------------
// Contrato de erro: Problem Details (RFC 9457) para toda falha.
// ---------------------------------------------------------------------------
// O padrao do ASP.NET Core so lanca BadHttpRequestException em Development.
// Fora dele, um corpo invalido virava 400 com corpo VAZIO - quebrando a
// promessa de que todo erro da API tem o mesmo formato justamente no ambiente
// onde ela mais importa. Ligado aqui, a falha passa pelo tratador central.
construtor.Services.Configure<RouteHandlerOptions>(opcoes => opcoes.ThrowOnBadRequest = true);

construtor.Services.AddProblemDetails(opcoes =>
{
    // Vale para toda resposta de erro, inclusive as que nao passam por
    // excecao (404 de rota inexistente, 405). Sem isto, o suporte teria um
    // identificador de correlacao em alguns erros e nao em outros.
    opcoes.CustomizeProblemDetails = contexto =>
    {
        var correlacao = contexto.HttpContext.RequestServices
            .GetService<IContextoDeCorrelacao>();

        if (!string.IsNullOrEmpty(correlacao?.IdDeCorrelacao))
        {
            contexto.ProblemDetails.Extensions["idDeCorrelacao"] = correlacao.IdDeCorrelacao;
        }

        contexto.ProblemDetails.Instance ??= contexto.HttpContext.Request.Path;
    };
});
construtor.Services.AddExceptionHandler<TratadorDeExcecoes>();

// ---------------------------------------------------------------------------
// Correlacao por requisicao.
// ---------------------------------------------------------------------------
construtor.Services.AddScoped<ContextoDeCorrelacao>();
construtor.Services.AddScoped<IContextoDeCorrelacao>(
    provedor => provedor.GetRequiredService<ContextoDeCorrelacao>());

// ---------------------------------------------------------------------------
// Infraestrutura: PostgreSQL, relogio e health check de banco.
// A API nao referencia EF Core nem Npgsql - a composicao acontece aqui.
// ---------------------------------------------------------------------------
construtor.Services.AdicionarInfraestrutura(construtor.Configuration);

var opcoesDeAutenticacao = InjecaoDeDependencia.LerOpcoesDeAutenticacao(construtor.Configuration);

// ---------------------------------------------------------------------------
// Identidade humana.
// ---------------------------------------------------------------------------
construtor.Services.AddHttpContextAccessor();
construtor.Services.AddScoped<IContextoDoUsuarioAtual, ContextoDoUsuarioAtual>();
construtor.Services.AddScoped<IContextoDaIntegracaoAtual, ContextoDaIntegracaoAtual>();

construtor.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opcoes =>
    {
        // Sem isto, o handler renomeia claims curtas para as URIs longas do
        // WS-Federation - "sub" vira ".../nameidentifier" - e a leitura por
        // nome original devolve vazio. O sintoma e traicoeiro: a autorizacao
        // passa (as claims proprias nao sao remapeadas) e so a identidade
        // some, virando 404 em vez de 401.
        opcoes.MapInboundClaims = false;

        opcoes.TokenValidationParameters =
            EmissorDeAccessToken.MontarParametrosDeValidacao(opcoesDeAutenticacao);

        // A resposta de falha e escrita pelo tratador central, para que 401
        // saia no mesmo formato Problem Details de todos os outros erros.
        opcoes.Events = new JwtBearerEvents
        {
            OnChallenge = contexto =>
            {
                contexto.HandleResponse();
                throw new CentralAntifraude.Application.Erros.NaoAutenticado(
                    "token_invalido",
                    "Credencial ausente, invalida ou expirada.");
            },
        };
    });

// Esquema separado para integracoes: uma API key nunca vira sessao humana, e
// um access token humano nao serve na ingestao (CLAUDE.md secao 50). A
// separacao fica visivel no codigo, no log e na configuracao de autorizacao.
construtor.Services
    .AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ManipuladorDeAutenticacaoDeIntegracao>(
        ManipuladorDeAutenticacaoDeIntegracao.Esquema,
        _ => { });

construtor.Services.AddAuthorization(opcoes =>
{
    PoliticasDeAutorizacao.Registrar(opcoes);
    EndpointsDeIngestao.RegistrarPoliticaDeIntegracao(opcoes);
});

// ---------------------------------------------------------------------------
// Limite de tentativas de login.
//
// Particionado por IP de origem. Ler o e-mail do corpo para compor a chave
// exigiria bufferizar a requisicao antes do roteamento - custo que so se
// justifica com evidencia de abuso distribuido, e nao agora.
//
// O limite protege contra forca bruta em uma conta. Nao protege contra
// password spraying vindo de muitos IPs: isso e superficie da Fase 11, junto
// do resto do hardening.
// ---------------------------------------------------------------------------
construtor.Services.AddRateLimiter(opcoes =>
{
    opcoes.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    opcoes.AddPolicy(EndpointsDeIdentidade.LimiteDeLogin, contexto =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ChaveDeLimiteDeLogin(contexto),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // A ingestao e particionada pela CREDENCIAL, e nao pelo IP: varias
    // instancias do integrador saem de IPs diferentes e sao o mesmo cliente,
    // e um limite por IP puniria uma delas sem proteger nada. Quando a
    // credencial nao pode ser lida (chave malformada ou ausente), a particao
    // cai no IP - que e justamente o caso de quem esta testando chaves.
    opcoes.AddPolicy(EndpointsDeIngestao.LimiteDeIngestao, contexto =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ChaveDeLimiteDeIngestao(contexto),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

if (construtor.Environment.IsDevelopment())
{
    construtor.Services.AddOpenApi();
}

var aplicacao = construtor.Build();

// ---------------------------------------------------------------------------
// Seed de desenvolvimento.
//
// So roda em Development e so quando Seed:SenhaPadrao esta configurado.
// Em producao nao existe caminho que crie usuario com senha conhecida.
// ---------------------------------------------------------------------------
if (aplicacao.Environment.IsDevelopment())
{
    using var escopoDeInicializacao = aplicacao.Services.CreateScope();
    await SeedDeDesenvolvimento.ExecutarAsync(
        escopoDeInicializacao.ServiceProvider,
        aplicacao.Configuration,
        CancellationToken.None);
}

// Primeiro middleware do pipeline, de proposito: o tratador de excecoes esta
// acima dele e so enxerga o escopo de log criado aqui se a correlacao vier
// antes. Invertida, a linha de log da falha sairia sem CorrelationId - que e
// justamente a linha que alguem vai procurar durante um incidente.
aplicacao.UseMiddleware<MiddlewareDeCorrelacao>();

aplicacao.UseExceptionHandler();

// Sem isto, um 404 de rota nao encontrada volta com corpo vazio, quebrando a
// promessa de que todo erro da API tem o mesmo formato.
aplicacao.UseStatusCodePages();

aplicacao.UseRateLimiter();

aplicacao.UseAuthentication();
aplicacao.UseAuthorization();

if (aplicacao.Environment.IsDevelopment())
{
    aplicacao.MapOpenApi();
}

aplicacao.MapearEndpointsDeAutenticacao();
aplicacao.MapearEndpointsDeUsuarios();
aplicacao.MapearEndpointsDeIntegracoes();
aplicacao.MapearEndpointsDeTransacoes();
aplicacao.MapearEndpointsDeRisco();
aplicacao.MapearEndpointsDeAlertas();
aplicacao.MapearEndpointsDeCasos();
aplicacao.MapearEndpointsDeBacktests();
aplicacao.MapearEndpointsDeIngestao();

// ---------------------------------------------------------------------------
// Saude.
// /health/live  -> o processo esta vivo. Nao toca em dependencia externa.
// /health/ready -> as dependencias respondem. Este pode falhar sozinho.
// Separar os dois evita que um banco lento faca o orquestrador reiniciar um
// processo que esta perfeitamente saudavel.
// ---------------------------------------------------------------------------
aplicacao.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = RespostaDeSaude.Escrever,
}).AllowAnonymous();

aplicacao.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registro => registro.Tags.Contains(InjecaoDeDependencia.TagDeProntidao),
    ResponseWriter = RespostaDeSaude.Escrever,
}).AllowAnonymous();

await aplicacao.RunAsync();

// Chave do limite de login: IP de origem.
static string ChaveDeLimiteDeLogin(HttpContext contexto) =>
    contexto.Connection.RemoteIpAddress?.ToString() ?? "sem-ip";

// Chave do limite de ingestao: o identificador PUBLICO da credencial - a
// parte da chave que nao e segredo. O segredo nunca vira chave de dicionario
// em memoria.
static string ChaveDeLimiteDeIngestao(HttpContext contexto)
{
    const string prefixo = ManipuladorDeAutenticacaoDeIntegracao.PrefixoDoEsquema;
    var cabecalho = contexto.Request.Headers.Authorization.ToString();

    if (cabecalho.StartsWith(prefixo, StringComparison.Ordinal))
    {
        // Split limitado a 3, pelo mesmo motivo do interpretador de credencial:
        // o segredo e base64url e pode conter "_".
        var partes = cabecalho[prefixo.Length..].Split('_', 3);

        if (partes.Length == 3)
        {
            return $"credencial:{partes[1]}";
        }
    }

    return $"ip:{contexto.Connection.RemoteIpAddress?.ToString() ?? "sem-ip"}";
}

/// <summary>
/// Exposto para que os testes de integracao possam hospedar a aplicacao real
/// com WebApplicationFactory, em vez de recriar a composicao por conta propria.
/// </summary>
public partial class Program;
