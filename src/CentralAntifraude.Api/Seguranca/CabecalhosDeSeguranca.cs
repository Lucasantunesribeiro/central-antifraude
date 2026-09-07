using Microsoft.AspNetCore.Http.Features;

namespace CentralAntifraude.Api.Seguranca;

/// <summary>
/// Os tres cabecalhos que fazem diferenca para uma API que so devolve JSON.
///
/// **A lista e curta de proposito.** CSP, HSTS e Permissions-Policy pertencem
/// a quem serve HTML — e quem serve o HTML da Central Antifraude e a Vercel, a
/// partir da Fase 14. Repetir aqui uma politica de conteudo para respostas que
/// nunca sao renderizadas seria configuracao sem problema para resolver
/// (CLAUDE.md secao 97).
///
/// O que sobra sao os tres que valem para JSON:
///
/// | Cabecalho | O que impede |
/// |---|---|
/// | `X-Content-Type-Options: nosniff` | o navegador adivinhar que um JSON e HTML e executa-lo |
/// | `X-Frame-Options: DENY` | a resposta ser embutida em um iframe de outro site |
/// | `Referrer-Policy: no-referrer` | o identificador de um caso vazar na URL de referencia ao clicar em um link |
///
/// O terceiro e o menos obvio e o mais concreto neste produto: os caminhos
/// carregam identificadores de transacao, caso e regra. Sem a politica, um
/// clique para fora levaria o caminho inteiro no `Referer`.
/// </summary>
public sealed class MiddlewareDeCabecalhosDeSeguranca
{
    private readonly RequestDelegate _proximo;

    public MiddlewareDeCabecalhosDeSeguranca(RequestDelegate proximo) => _proximo = proximo;

    public Task InvokeAsync(HttpContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);

        // Escritos ANTES de o corpo comecar a sair. Depois do primeiro byte os
        // cabecalhos ja foram enviados e a atribuicao seria silenciosamente
        // perdida — o tipo de defeito que so aparece em resposta grande.
        contexto.Response.OnStarting(static estado =>
        {
            var resposta = ((HttpContext)estado).Response;

            resposta.Headers["X-Content-Type-Options"] = "nosniff";
            resposta.Headers["X-Frame-Options"] = "DENY";
            resposta.Headers["Referrer-Policy"] = "no-referrer";

            return Task.CompletedTask;
        }, contexto);

        return _proximo(contexto);
    }
}

/// <summary>
/// Recusa corpo grande demais **antes** de ele ser lido.
///
/// **Tres camadas, e cada uma cobre o buraco da anterior.**
///
/// 1. `Content-Length` acima do teto e recusado aqui, sem ler um byte. E o
///    caminho de 99% das requisicoes e o unico que vale igual em producao e no
///    host de teste — a opcao do Kestrel nao existe no `TestServer`, e um
///    limite sem teste e a pior forma de ter um limite.
/// 2. <see cref="IHttpMaxRequestBodySizeFeature"/> cobre o corpo em
///    `chunked`, que chega sem `Content-Length`.
/// 3. `KestrelServerOptions.Limits` e a rede de seguranca do servidor real.
///
/// O teto e apertado por rota, e nao afrouxado: a ingestao declara 8 KB no
/// proprio endpoint, e essa metadata e aplicada depois do roteamento.
///
/// O status e `413`, e nao `400`. A diferenca importa para quem integra:
/// `400` manda depurar o JSON, `413` manda mandar menos.
/// </summary>
public sealed class MiddlewareDeLimiteDeCorpo
{
    private readonly RequestDelegate _proximo;
    private readonly long _limite;

    public MiddlewareDeLimiteDeCorpo(RequestDelegate proximo, long limite)
    {
        _proximo = proximo;
        _limite = limite;
    }

    public async Task InvokeAsync(HttpContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);

        var feature = contexto.Features.Get<IHttpMaxRequestBodySizeFeature>();

        // `IsReadOnly` fica verdadeiro depois que a leitura do corpo comeca.
        // Escrever ali lancaria — e derrubar uma requisicao legitima para
        // aplicar um limite seria o oposto do objetivo.
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = _limite;
        }

        if (contexto.Request.ContentLength > _limite)
        {
            // Sem `throw`: uma excecao aqui viraria `400` no tratador central,
            // e `400` diria ao integrador que o JSON esta errado.
            contexto.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            contexto.Response.ContentType = "application/problem+json";

            await contexto.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.5.14",
                title = "Corpo grande demais",
                status = StatusCodes.Status413PayloadTooLarge,
                detail = $"O corpo da requisicao passa do limite de {_limite} bytes.",
                instance = contexto.Request.Path.Value,
                codigo = "corpo_grande_demais",
            });

            return;
        }

        await _proximo(contexto);
    }
}
