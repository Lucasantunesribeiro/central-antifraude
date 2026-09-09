using CentralAntifraude.Api.Identidade;
using CentralAntifraude.Api.Integracoes;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// A matriz de autorizacao, verificada contra as rotas que a aplicacao
/// realmente expoe (ROADMAP 11.6).
///
/// **O risco que este teste fecha nao e uma rota mal protegida — e uma rota
/// NOVA.** Revisar autorizacao rota a rota funciona uma vez; o que nao
/// funciona e lembrar de revisar de novo a cada fase. Aqui a lista de rotas
/// nao e escrita a mao: ela e lida do <see cref="EndpointDataSource"/> da
/// aplicacao real, e comparada com a tabela declarada abaixo.
///
/// Uma rota que aparecer sem entrada na tabela **quebra a build**. Uma rota
/// cuja politica mudar sem a tabela mudar junto, tambem. E o custo de manter a
/// tabela e exatamente o ponto: quem cria a rota precisa declarar quem a
/// alcanca.
///
/// A tabela e a fonte de <c>docs/matriz-de-autorizacao.md</c>.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class MatrizDeAutorizacaoTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;

    public MatrizDeAutorizacaoTests(FixtureDoBanco banco) => _banco = banco;

    /// <summary>Rota anonima de proposito, com a razao ao lado.</summary>
    private const string Anonima = "(anonima)";

    /// <summary>
    /// A matriz declarada: verbo e rota para a politica que a alcanca.
    ///
    /// Ler esta tabela responde, em um lugar so, "quem chega em quê" — que e o
    /// que o ROADMAP 11.6 pede.
    /// </summary>
    private static readonly Dictionary<string, string> Matriz = new(StringComparer.Ordinal)
    {
        // -------------------------------------------------------------------
        // Saude e sessao: anonimas por natureza.
        //
        // Health checks precisam responder antes de existir identidade — e um
        // orquestrador que precisasse de token para saber se o processo esta
        // vivo nao teria como obte-lo. Login e refresh sao anonimos porque a
        // identidade e o RESULTADO deles; a prova ali e a senha ou o cookie,
        // e as duas rotas tem limite de tentativas proprio.
        // -------------------------------------------------------------------
        // O `*` e literal: health checks respondem a qualquer verbo, porque
        // nao declaram metodo. Escrever "GET" aqui seria mais bonito e falso.
        ["* /health/live"] = Anonima,
        ["* /health/ready"] = Anonima,
        ["POST /api/auth/login"] = Anonima,
        ["POST /api/auth/demo"] = Anonima,
        ["POST /api/auth/refresh"] = Anonima,
        ["POST /api/auth/logout"] = Anonima,

        ["GET /api/auth/eu"] = PoliticasDeAutorizacao.QualquerPerfil,

        // -------------------------------------------------------------------
        // Ingestao: esquema proprio de integracao, nunca sessao humana.
        // -------------------------------------------------------------------
        ["POST /api/ingestao/transacoes"] = EndpointsDeIngestao.PoliticaDeIntegracao,

        // -------------------------------------------------------------------
        // Administracao: usuarios, integracoes e credenciais.
        // -------------------------------------------------------------------
        ["GET /api/usuarios"] = PoliticasDeAutorizacao.Administrador,
        ["GET /api/usuarios/{id:guid}"] = PoliticasDeAutorizacao.Administrador,
        ["POST /api/usuarios"] = PoliticasDeAutorizacao.Administrador,
        ["PUT /api/usuarios/{id:guid}/perfil"] = PoliticasDeAutorizacao.Administrador,
        ["PUT /api/usuarios/{id:guid}/nome"] = PoliticasDeAutorizacao.Administrador,
        ["PUT /api/usuarios/{id:guid}/ativacao"] = PoliticasDeAutorizacao.Administrador,
        ["PUT /api/usuarios/{id:guid}/senha"] = PoliticasDeAutorizacao.Administrador,

        ["GET /api/integracoes"] = PoliticasDeAutorizacao.Administrador,
        ["GET /api/integracoes/{id:guid}"] = PoliticasDeAutorizacao.Administrador,
        ["POST /api/integracoes"] = PoliticasDeAutorizacao.Administrador,
        ["PUT /api/integracoes/{id:guid}/nome"] = PoliticasDeAutorizacao.Administrador,
        ["PUT /api/integracoes/{id:guid}/ativacao"] = PoliticasDeAutorizacao.Administrador,
        ["POST /api/integracoes/{id:guid}/credenciais"] = PoliticasDeAutorizacao.Administrador,
        ["DELETE /api/integracoes/{id:guid}/credenciais/{credencialId:guid}"] =
            PoliticasDeAutorizacao.Administrador,

        // -------------------------------------------------------------------
        // Leitura operacional: qualquer perfil autenticado, inclusive Auditor.
        //
        // Consultar decisao e o trabalho do auditor, e o analista precisa das
        // regras vigentes para entender o proprio score. Fechar a leitura
        // junto com a escrita seria simples e errado.
        // -------------------------------------------------------------------
        ["GET /api/transacoes"] = PoliticasDeAutorizacao.QualquerPerfil,
        ["GET /api/transacoes/{id:guid}"] = PoliticasDeAutorizacao.QualquerPerfil,
        ["GET /api/regras"] = PoliticasDeAutorizacao.QualquerPerfil,
        ["GET /api/regras/perfil"] = PoliticasDeAutorizacao.QualquerPerfil,
        ["GET /api/alertas"] = PoliticasDeAutorizacao.QualquerPerfil,
        ["GET /api/casos"] = PoliticasDeAutorizacao.QualquerPerfil,
        ["GET /api/casos/{id:guid}"] = PoliticasDeAutorizacao.QualquerPerfil,
        ["GET /api/painel"] = PoliticasDeAutorizacao.QualquerPerfil,
        ["GET /api/painel/regras"] = PoliticasDeAutorizacao.QualquerPerfil,

        // -------------------------------------------------------------------
        // Operacao de fraude: Auditor fica de fora.
        //
        // Agir sobre alerta ou caso e acao operacional; o perfil do Auditor e
        // de leitura (ROADMAP 1.9).
        // -------------------------------------------------------------------
        ["POST /api/casos"] = PoliticasDeAutorizacao.OperacaoDeFraude,
        ["POST /api/casos/{id:guid}/alertas"] = PoliticasDeAutorizacao.OperacaoDeFraude,
        ["POST /api/casos/{id:guid}/assumir"] = PoliticasDeAutorizacao.OperacaoDeFraude,
        ["POST /api/casos/{id:guid}/transferir"] = PoliticasDeAutorizacao.OperacaoDeFraude,
        ["POST /api/casos/{id:guid}/notas"] = PoliticasDeAutorizacao.OperacaoDeFraude,
        ["POST /api/casos/{id:guid}/resolucao"] = PoliticasDeAutorizacao.OperacaoDeFraude,

        // -------------------------------------------------------------------
        // Supervisao: mexer no motor e executar backtest.
        // -------------------------------------------------------------------
        ["GET /api/regras/tipos"] = PoliticasDeAutorizacao.Supervisor,
        ["GET /api/regras/gestao"] = PoliticasDeAutorizacao.Supervisor,
        ["GET /api/regras/{id:guid}"] = PoliticasDeAutorizacao.Supervisor,
        ["POST /api/regras"] = PoliticasDeAutorizacao.Supervisor,
        ["PUT /api/regras/{id:guid}/rascunho"] = PoliticasDeAutorizacao.Supervisor,
        ["DELETE /api/regras/{id:guid}/rascunho"] = PoliticasDeAutorizacao.Supervisor,
        ["POST /api/regras/{id:guid}/publicacao"] = PoliticasDeAutorizacao.Supervisor,
        ["POST /api/regras/{id:guid}/ativacao"] = PoliticasDeAutorizacao.Supervisor,
        ["POST /api/regras/perfil/limiares"] = PoliticasDeAutorizacao.Supervisor,

        ["GET /api/backtests"] = PoliticasDeAutorizacao.Supervisor,
        ["GET /api/backtests/{id:guid}"] = PoliticasDeAutorizacao.Supervisor,
        ["POST /api/backtests"] = PoliticasDeAutorizacao.Supervisor,
        ["POST /api/backtests/{id:guid}/cancelamento"] = PoliticasDeAutorizacao.Supervisor,

        // -------------------------------------------------------------------
        // Auditoria: Administrador e Auditor, e ninguem mais.
        //
        // A trilha e um controle SOBRE quem opera. Dar a quem e auditado o
        // poder de varrer o proprio rastro enfraquece o unico registro que
        // responde "quem fez o que e quando".
        // -------------------------------------------------------------------
        ["GET /api/auditoria"] = PoliticasDeAutorizacao.LeituraDeAuditoria,
        ["GET /api/auditoria/operacoes"] = PoliticasDeAutorizacao.LeituraDeAuditoria,
    };

    public ValueTask InitializeAsync()
    {
        _fabrica = new FabricaDaApi(_banco.StringDeConexao);

        // Basta um cliente para o host levantar e o roteamento ser construido.
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cliente?.Dispose();

        if (_fabrica is not null)
        {
            await _fabrica.DisposeAsync();
        }
    }

    [Fact]
    public void Toda_rota_exposta_tem_uma_decisao_de_autorizacao_declarada()
    {
        var reais = RotasReais();

        var semDeclaracao = reais.Keys.Except(Matriz.Keys, StringComparer.Ordinal).Order().ToList();

        Assert.True(
            semDeclaracao.Count == 0,
            "Rota(s) exposta(s) sem entrada na matriz de autorizacao. Quem cria a rota " +
            "precisa declarar quem a alcanca, e atualizar docs/matriz-de-autorizacao.md: "
                + string.Join(" | ", semDeclaracao));
    }

    [Fact]
    public void A_matriz_nao_declara_rota_que_nao_existe_mais()
    {
        // Uma entrada orfa e tao ruim quanto uma faltando: ela faz a matriz
        // parecer mais completa do que e, e o documento passa a descrever uma
        // API que nao existe.
        var reais = RotasReais();

        var orfas = Matriz.Keys.Except(reais.Keys, StringComparer.Ordinal).Order().ToList();

        Assert.True(
            orfas.Count == 0,
            "Entrada(s) na matriz sem rota correspondente: " + string.Join(" | ", orfas));
    }

    [Fact]
    public void Cada_rota_carrega_exatamente_a_politica_declarada()
    {
        var reais = RotasReais();

        var divergentes = reais
            .Where(rota => Matriz.TryGetValue(rota.Key, out var esperada) && esperada != rota.Value)
            .Select(rota => $"{rota.Key}: declarada '{Matriz[rota.Key]}', exposta '{rota.Value}'")
            .Order()
            .ToList();

        Assert.True(
            divergentes.Count == 0,
            "Rota(s) com politica diferente da declarada: " + string.Join(" | ", divergentes));
    }

    [Fact]
    public void Nenhuma_rota_de_api_e_anonima_alem_das_de_sessao()
    {
        // Falha fechada com nome: a politica de fallback ja exige autenticacao
        // em quem esquece de declarar, mas `AllowAnonymous` e uma escolha
        // explicita — e escolhas explicitas precisam de justificativa
        // explicita. As tres unicas sao login, refresh e logout, onde a
        // identidade e o resultado da operacao.
        var anonimas = RotasReais()
            .Where(rota => rota.Value == Anonima)
            .Select(rota => rota.Key)
            .Order()
            .ToList();

        Assert.Equal(
            [
                "* /health/live",
                "* /health/ready",
                "POST /api/auth/demo",
                "POST /api/auth/login",
                "POST /api/auth/logout",
                "POST /api/auth/refresh",
            ],
            anonimas);
    }

    [Fact]
    public void Toda_rota_de_escrita_humana_exige_perfil_mais_estreito_que_leitura()
    {
        // `QualquerPerfil` inclui o Auditor, cujo perfil e de leitura
        // (CLAUDE.md secao 8.3). Uma rota de escrita com essa politica daria
        // ao auditor poder de alterar o que ele deveria apenas conferir.
        var escritasAbertas = RotasReais()
            .Where(rota => !rota.Key.StartsWith("GET ", StringComparison.Ordinal))
            .Where(rota => rota.Value == PoliticasDeAutorizacao.QualquerPerfil)
            .Select(rota => rota.Key)
            .Order()
            .ToList();

        Assert.True(
            escritasAbertas.Count == 0,
            "Rota(s) de escrita alcancaveis por qualquer perfil, inclusive Auditor: "
                + string.Join(" | ", escritasAbertas));
    }

    /// <summary>
    /// As rotas que a aplicacao expoe de verdade, lidas do roteamento.
    ///
    /// Lidas, e nao escritas a mao: uma lista mantida manualmente descreveria
    /// a API que alguem lembrou de anotar, e nao a que esta no ar.
    /// </summary>
    private Dictionary<string, string> RotasReais()
    {
        var fonte = _fabrica.Services.GetRequiredService<EndpointDataSource>();
        var rotas = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var endpoint in fonte.Endpoints.OfType<RouteEndpoint>())
        {
            // A barra final some: `MapGet("/")` dentro de um grupo produz
            // "/api/casos/", e a matriz precisa ser legivel por gente — o
            // documento derivado dela e lido, nao so executado.
            var caminho = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).Trim('/');

            var metodos = endpoint.Metadata
                .GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()
                ?.HttpMethods ?? ["*"];

            var politica = PoliticaDe(endpoint);

            foreach (var metodo in metodos)
            {
                rotas[$"{metodo} {caminho}"] = politica;
            }
        }

        return rotas;
    }

    private static string PoliticaDe(RouteEndpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return Anonima;
        }

        // A ULTIMA politica declarada vence, e e isso que o ASP.NET Core
        // aplica quando um grupo aninhado redeclara autorizacao — o caso das
        // rotas de supervisao dentro do grupo de leitura de regras.
        var politicas = endpoint.Metadata
            .OfType<IAuthorizeData>()
            .Select(dado => dado.Policy)
            .Where(politica => !string.IsNullOrEmpty(politica))
            .ToList();

        return politicas.Count > 0 ? politicas[^1]! : "(fallback: autenticado)";
    }
}
