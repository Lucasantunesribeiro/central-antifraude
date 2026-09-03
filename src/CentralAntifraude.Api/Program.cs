using System.Text.Json;
using System.Text.Json.Serialization;
using CentralAntifraude.Api.Correlacao;
using CentralAntifraude.Api.Diagnostico;
using CentralAntifraude.Api.Erros;
using CentralAntifraude.Application.Correlacao;
using CentralAntifraude.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

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

if (construtor.Environment.IsDevelopment())
{
    construtor.Services.AddOpenApi();
}

var aplicacao = construtor.Build();

// Primeiro middleware do pipeline, de proposito: o tratador de excecoes esta
// acima dele e so enxerga o escopo de log criado aqui se a correlacao vier
// antes. Invertida, a linha de log da falha sairia sem CorrelationId - que e
// justamente a linha que alguem vai procurar durante um incidente.
aplicacao.UseMiddleware<MiddlewareDeCorrelacao>();

aplicacao.UseExceptionHandler();

// Sem isto, um 404 de rota nao encontrada volta com corpo vazio, quebrando a
// promessa de que todo erro da API tem o mesmo formato.
aplicacao.UseStatusCodePages();

if (aplicacao.Environment.IsDevelopment())
{
    aplicacao.MapOpenApi();
}

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
});

aplicacao.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registro => registro.Tags.Contains(InjecaoDeDependencia.TagDeProntidao),
    ResponseWriter = RespostaDeSaude.Escrever,
});

await aplicacao.RunAsync();

/// <summary>
/// Exposto para que os testes de integracao possam hospedar a aplicacao real
/// com WebApplicationFactory, em vez de recriar a composicao por conta propria.
/// </summary>
public partial class Program;
