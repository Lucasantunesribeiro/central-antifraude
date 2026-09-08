using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using CentralAntifraude.Application.Observabilidade;
using Microsoft.AspNetCore.Diagnostics;

namespace CentralAntifraude.Api.Observabilidade;

/// <summary>
/// Uma linha de log e duas metricas por requisicao.
///
/// **O que faltava.** Ate a Fase 11 o sistema logava excecoes, recusas de
/// credencial e ciclos de fundo — tudo o que da errado. O que estava ausente
/// era o normal: quantas requisicoes existem, quais rotas, quanto demoram. Sem
/// isso, "a API esta lenta" nao tem como ser respondido, e uma degradacao so
/// aparece quando vira erro.
///
/// **A operacao e o PADRAO da rota, e nunca o caminho.**
/// <c>GET /api/casos/{id}</c>, e nao <c>GET /api/casos/9f3c...</c>. Isso vale
/// como regra de cardinalidade — o caminho concreto criaria uma serie de
/// metrica por recurso — mas o motivo mais forte e outro: o caminho carrega
/// identificador de recurso de um tenant, e metrica nao tem tenant nem
/// autorizacao. O mesmo raciocinio exclui a query string, que nesta API carrega
/// termo de busca digitado por um analista.
///
/// **O endpoint so e conhecido depois.** O roteamento roda adiante deste
/// middleware, entao a rota casada e lida na volta — e uma requisicao que nao
/// casou com rota nenhuma vira <c>desconhecida</c>, em vez de virar uma serie
/// nova para cada URL que um varredor de vulnerabilidade tentar.
/// </summary>
public sealed partial class MiddlewareDeTelemetria
{
    private static readonly Meter Medidor = new(Telemetria.MedidorDaApi);

    private static readonly Counter<long> Requisicoes =
        Medidor.CreateCounter<long>(Telemetria.Instrumentos.RequisicoesTotal);

    private static readonly Histogram<double> Duracao =
        Medidor.CreateHistogram<double>(Telemetria.Instrumentos.RequisicaoDuracao);

    private readonly RequestDelegate _proximo;
    private readonly ILogger<MiddlewareDeTelemetria> _log;

    public MiddlewareDeTelemetria(RequestDelegate proximo, ILogger<MiddlewareDeTelemetria> log)
    {
        _proximo = proximo;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);

        // Saude fica de fora. Um orquestrador consulta liveness a cada poucos
        // segundos, para sempre: incluir isso encheria o log de linhas que
        // ninguem le e daria a media de latencia da API o formato do health
        // check (CLAUDE.md secao 78).
        if (contexto.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
        {
            await _proximo(contexto);
            return;
        }

        var inicio = Stopwatch.GetTimestamp();

        try
        {
            await _proximo(contexto);
        }
        finally
        {
            var decorrido = Stopwatch.GetElapsedTime(inicio).TotalMilliseconds;
            var operacao = OperacaoDe(contexto);
            var status = contexto.Response.StatusCode;

            var etiquetas = new TagList
            {
                { "operacao", operacao },
                { "resultado", FaixaDe(status) },
            };

            Requisicoes.Add(1, etiquetas);
            Duracao.Record(decorrido, new TagList { { "operacao", operacao } });

            // 5xx sobe de nivel. E a unica classe de resposta que significa
            // defeito do produto: 400 e 404 sao o contrato funcionando, e
            // registra-los como aviso treinaria qualquer pessoa a ignorar
            // avisos.
            if (status >= 500)
            {
                RegistrarFalha(_log, operacao, status, decorrido);
            }
            else
            {
                RegistrarRequisicao(_log, operacao, status, decorrido);
            }
        }
    }

    /// <summary>
    /// Metodo mais padrao da rota casada, ou <c>desconhecida</c>.
    ///
    /// O metodo entra junto porque <c>GET /api/casos/{id}</c> e
    /// <c>POST /api/casos/{id}</c> sao operacoes diferentes com perfis de
    /// latencia diferentes, e uma serie so esconderia isso.
    ///
    /// **O segundo caminho de leitura nao e defensivo — e obrigatorio.** O
    /// tratador de excecoes LIMPA o endpoint antes de reexecutar o pipeline
    /// para produzir a resposta de erro. Sem a leitura pelo
    /// <c>IExceptionHandlerFeature</c>, toda requisicao que termina em erro de
    /// dominio — 404, 409, 400 — seria registrada como <c>desconhecida</c>:
    /// justamente as requisicoes que alguem vai querer investigar depois
    /// perderiam o nome da operacao. Isto foi descoberto por um teste que
    /// esperava <c>GET /api/casos/{id}</c> num 404 e recebeu
    /// <c>desconhecida</c>.
    /// </summary>
    private static string OperacaoDe(HttpContext contexto)
    {
        var endpoint = contexto.GetEndpoint()
            ?? contexto.Features.Get<IExceptionHandlerFeature>()?.Endpoint;

        var rota = (endpoint as RouteEndpoint)?.RoutePattern.RawText;

        if (string.IsNullOrEmpty(rota))
        {
            return "desconhecida";
        }

        // O padrao de uma rota declarada dentro de um grupo volta sem a barra
        // inicial. Normalizar aqui evita que a mesma rota apareca de dois
        // jeitos no log conforme o modo como foi registrada.
        var caminho = rota.StartsWith('/') ? rota : "/" + rota;

        return $"{contexto.Request.Method} {caminho}";
    }

    /// <summary>
    /// Faixa da resposta: <c>2xx</c>, <c>4xx</c>, <c>5xx</c>.
    ///
    /// O codigo exato viraria dimensao com dezenas de valores e nao mudaria
    /// nenhuma decisao operacional — quem investiga um 409 especifico vai ao
    /// log, que tem o numero inteiro.
    /// </summary>
    private static string FaixaDe(int status) =>
        string.Create(CultureInfo.InvariantCulture, $"{status / 100}xx");

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Information,
        Message = "{Operacao} respondeu {Status} em {DuracaoEmMs:0.0} ms.")]
    private static partial void RegistrarRequisicao(
        ILogger logger,
        string operacao,
        int status,
        double duracaoEmMs);

    [LoggerMessage(
        EventId = 101,
        Level = LogLevel.Warning,
        Message = "{Operacao} respondeu {Status} em {DuracaoEmMs:0.0} ms.")]
    private static partial void RegistrarFalha(
        ILogger logger,
        string operacao,
        int status,
        double duracaoEmMs);
}
