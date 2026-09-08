using CentralAntifraude.Application.Identidade;

namespace CentralAntifraude.Api.Identidade;

/// <summary>
/// Onde o refresh token vive no navegador, e como o servidor se defende de
/// CSRF nos endpoints que dependem dele.
///
/// **Por que cookie e nao o corpo da resposta.** O ROADMAP (secao 1.3) proibe
/// localStorage para o refresh token, e por um motivo concreto: qualquer XSS
/// na aplicacao leria o localStorage e levaria a sessao inteira. Um cookie
/// `HttpOnly` nao e visivel ao JavaScript — o XSS ainda faz estrago, mas nao
/// consegue exfiltrar a credencial de longa duracao.
///
/// O access token vai pelo corpo e fica so na memoria do JavaScript. Nada de
/// sessao e escrito em disco pelo navegador alem deste cookie.
///
/// **Path restrito.** O cookie so e enviado nas rotas de sessao. Ele nao
/// acompanha as chamadas normais da API, entao nao aparece em log de proxy de
/// requisicao comum nem e exposto por um endpoint que reflita cabecalhos.
/// </summary>
public static class SessaoHttp
{
    /// <summary>
    /// Teto do corpo de qualquer requisicao, em bytes.
    ///
    /// O padrao do Kestrel e 30 MB. Nenhuma rota deste produto precisa disso:
    /// a maior entrada humana e uma nota de investigacao de 4.000 caracteres,
    /// e a ingestao tem teto proprio de 8 KB. Sem o limite global, um corpo de
    /// 30 MB seria lido inteiro antes de qualquer validacao — memoria gasta
    /// para ser recusada depois.
    /// </summary>
    public const long TamanhoMaximoDoCorpo = 64 * 1024;

    public const string NomeDoCookie = "ca_sessao";

    /// <summary>Prefixo comum de todas as rotas de aplicacao da API.</summary>
    public const string PrefixoDaApi = "/api";

    /// <summary>Prefixo das rotas de sessao — tambem o Path do cookie.</summary>
    public const string CaminhoDaSessao = PrefixoDaApi + "/auth";

    public static void EscreverCookieDeSessao(
        HttpContext contexto,
        string refreshTokenBruto,
        DateTimeOffset expiraEm)
    {
        ArgumentNullException.ThrowIfNull(contexto);

        contexto.Response.Cookies.Append(NomeDoCookie, refreshTokenBruto, MontarOpcoes(contexto, expiraEm));
    }

    public static string? LerCookieDeSessao(HttpContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);

        return contexto.Request.Cookies[NomeDoCookie];
    }

    public static void LimparCookieDeSessao(HttpContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);

        // As opcoes precisam bater com as da escrita (Path, SameSite, Secure),
        // senao o navegador trata como outro cookie e o antigo sobrevive.
        var opcoes = MontarOpcoes(contexto, DateTimeOffset.UnixEpoch);
        contexto.Response.Cookies.Delete(NomeDoCookie, opcoes);
    }

    /// <summary>
    /// Interna, e nao privada, para que um teste possa afirmar a decisao mais
    /// delicada deste arquivo: `SameSite` em conexao segura. Essa escolha so se
    /// manifesta num deploy cross-site real, longe do alcance dos testes de
    /// integracao que rodam em HTTP.
    /// </summary>
    internal static CookieOptions MontarOpcoes(HttpContext contexto, DateTimeOffset expiraEm)
    {
        var conexaoSegura = contexto.Request.IsHttps;

        return new CookieOptions
        {
            // Fora do alcance do JavaScript. E a razao de o cookie existir.
            HttpOnly = true,

            // Em desenvolvimento o frontend fala com a API pelo proxy do Vite,
            // em http://localhost. Marcar Secure ali faria o navegador
            // descartar o cookie em silencio e o login "nao funcionar" sem
            // erro nenhum. Em qualquer conexao HTTPS o cookie e Secure.
            Secure = conexaoSegura,

            // O ponto mais sutil de todo o desenho de sessao, e o que so
            // apareceu no deploy real.
            //
            // Em desenvolvimento, frontend e API compartilham origem pelo proxy
            // do `vite dev`, e `Lax` basta. Em producao eles estao em SITES
            // DIFERENTES — o front em `vercel.app`, a API numa Function URL em
            // `on.aws` —, e o navegador NAO envia um cookie `Lax` numa
            // requisicao `fetch` cross-site. O efeito: o login funciona na
            // sessao corrente (o access token fica em memoria), mas o `refresh`
            // que o app dispara ao montar nao recebe o cookie, e um F5 desloga.
            //
            // `None` e o unico valor que faz o cookie viajar cross-site, e ele
            // EXIGE `Secure` — por isso a escolha e amarrada a conexao segura.
            // Em HTTP local, `None` sem `Secure` seria descartado em silencio,
            // entao ali continua `Lax`.
            //
            // `None` abre a porta que `Lax` fechava contra CSRF. Quem a fecha de
            // volta e a verificacao de `Origin` em `OrigemEhConfiavel`, logo
            // abaixo — e e por isso que o comentario dela diz que vale "nos dois
            // modelos". O desenho cross-site nao afrouxa a seguranca; ele move a
            // defesa do cookie para o cabecalho, que e onde ela precisa estar
            // quando o cookie tem de cruzar sites.
            SameSite = conexaoSegura ? SameSiteMode.None : SameSiteMode.Lax,

            Path = CaminhoDaSessao,
            Expires = expiraEm,
            IsEssential = true,
        };
    }

    /// <summary>
    /// Defesa contra CSRF nos endpoints que usam o cookie.
    ///
    /// O cabecalho <c>Origin</c> e preenchido pelo navegador e nao pode ser
    /// alterado por JavaScript de outra pagina. Se ele vier e nao for uma
    /// origem conhecida, a requisicao nao partiu da nossa aplicacao.
    ///
    /// Requisicao sem <c>Origin</c> e aceita: clientes que nao sao navegador
    /// (curl, teste de integracao) nao enviam o cabecalho — e tambem nao
    /// carregam cookie de terceiros automaticamente, que e a premissa do
    /// ataque. Recusar por ausencia bloquearia clientes legitimos sem fechar
    /// nenhum vetor.
    /// </summary>
    public static bool OrigemEhConfiavel(HttpContext contexto, OpcoesDeAutenticacao opcoes)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentNullException.ThrowIfNull(opcoes);

        var origem = contexto.Request.Headers.Origin.ToString();

        if (string.IsNullOrEmpty(origem))
        {
            return true;
        }

        if (opcoes.OrigensPermitidas.Contains(origem, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // Mesma origem do proprio pedido: e o caso do `vite dev` com proxy,
        // em que o navegador enxerga frontend e API no mesmo host.
        var propria = $"{contexto.Request.Scheme}://{contexto.Request.Host}";

        return string.Equals(origem, propria, StringComparison.OrdinalIgnoreCase);
    }
}
