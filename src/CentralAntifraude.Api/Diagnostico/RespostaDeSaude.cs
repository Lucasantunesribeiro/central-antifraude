using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CentralAntifraude.Api.Diagnostico;

/// <summary>
/// Escreve a resposta dos health checks.
///
/// Deliberadamente pobre em informacao: nome do componente e estado, nada
/// mais. A mensagem de excecao de um health check de banco costuma conter
/// host, porta, nome do banco e usuario - e o endpoint de saude e a
/// superficie mais exposta de uma aplicacao (CLAUDE.md secao 12.5).
/// </summary>
public static class RespostaDeSaude
{
    public static Task Escrever(HttpContext contexto, HealthReport relatorio)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentNullException.ThrowIfNull(relatorio);

        contexto.Response.ContentType = "application/json; charset=utf-8";

        var corpo = new
        {
            estado = relatorio.Status.ToString(),
            duracaoEmMs = (long)relatorio.TotalDuration.TotalMilliseconds,
            componentes = relatorio.Entries.ToDictionary(
                entrada => entrada.Key,
                entrada => entrada.Value.Status.ToString(),
                StringComparer.Ordinal),
        };

        return contexto.Response.WriteAsync(
            JsonSerializer.Serialize(corpo, OpcoesJson.Padrao),
            contexto.RequestAborted);
    }

    private static class OpcoesJson
    {
        public static readonly JsonSerializerOptions Padrao = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }
}
