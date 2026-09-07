using CentralAntifraude.Api.Correlacao;
using CentralAntifraude.Application.Identidade;

namespace CentralAntifraude.Api.Seguranca;

/// <summary>
/// CORS para o modelo de deploy planejado.
///
/// **Por que agora, e nao antes.** Ate a Fase 10 o `vite dev` fazia proxy e o
/// navegador enxergava tudo na mesma origem — CORS teria sido configuracao sem
/// problema para resolver. A Fase 14 coloca o frontend na Vercel e a API numa
/// Function URL: dominios diferentes, cookie de sessao atravessando, e o
/// ROADMAP 11.4 pede exatamente que isso seja validado antes do deploy.
///
/// **A lista de origens e a MESMA da verificacao de Origin** que ja defende
/// contra CSRF desde a Fase 1 (<c>Autenticacao:OrigensPermitidas</c>). Duas
/// listas sairiam de sincronia no primeiro ajuste, e o sintoma seria o pior
/// possivel: o navegador aceitaria a resposta e o servidor recusaria a
/// operacao, ou vice-versa.
///
/// **Nada de curinga.** `AllowAnyOrigin` e `AllowCredentials` sao
/// incompativeis por decisao da propria especificacao, e por um bom motivo:
/// permitir credencial de qualquer origem entrega a sessao a qualquer site.
/// Aqui a lista e explicita ou o CORS nao existe.
///
/// **Lista vazia desliga o CORS.** E o estado de desenvolvimento, onde o proxy
/// do Vite faz tudo parecer mesma origem. Ligar CORS ali so criaria uma
/// configuracao que ninguem exercita.
/// </summary>
public static class PoliticaDeCors
{
    public const string Nome = "frontend-autorizado";

    /// <summary>
    /// Cabecalhos que o navegador pode LER da resposta.
    ///
    /// Por padrao ele so enxerga um punhado de cabecalhos simples. Sem expor
    /// a correlacao, o cliente HTTP do frontend nao conseguiria mostrar o
    /// codigo que o suporte pede quando algo falha.
    /// </summary>
    public static readonly string[] CabecalhosExpostos = [MiddlewareDeCorrelacao.NomeDoCabecalho];

    public static void Registrar(
        Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions opcoes,
        OpcoesDeAutenticacao autenticacao)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(autenticacao);

        opcoes.AddPolicy(Nome, politica =>
        {
            if (autenticacao.OrigensPermitidas.Length == 0)
            {
                // Sem origens declaradas, nenhuma origem cruzada e aceita. A
                // politica existe mas nao autoriza ninguem — que e o
                // comportamento certo para mesma origem.
                politica.WithOrigins([]);
                return;
            }

            politica
                .WithOrigins(autenticacao.OrigensPermitidas)
                // O cookie de refresh precisa atravessar; sem isto o navegador
                // nao o envia e a sessao morre no primeiro F5 do deploy
                // cross-site.
                .AllowCredentials()
                // Lista fechada, e nao `AllowAnyMethod`: o produto usa cinco
                // verbos, e um curinga aceitaria qualquer verbo que uma rota
                // futura viesse a expor sem ninguem decidir.
                .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                .WithHeaders(
                    "Authorization",
                    "Content-Type",
                    "Accept",
                    MiddlewareDeCorrelacao.NomeDoCabecalho,
                    "Idempotency-Key")
                .WithExposedHeaders(CabecalhosExpostos);
        });
    }
}
