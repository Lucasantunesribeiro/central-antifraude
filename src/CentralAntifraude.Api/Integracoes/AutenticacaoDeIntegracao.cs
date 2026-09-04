using System.Security.Claims;
using System.Text.Encodings.Web;
using CentralAntifraude.Application.Integracoes;
using CentralAntifraude.Domain.Tempo;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CentralAntifraude.Api.Integracoes;

/// <summary>
/// Autentica uma integracao pela credencial dela.
///
/// **Esquema proprio, e nao Bearer.** O cabecalho e
/// <c>Authorization: ApiKey caf_..._...</c>. CLAUDE.md secao 50 exige que
/// integracoes nao finjam ser usuarios humanos, e o esquema separado torna
/// isso visivel em todo lugar: no codigo, no log e na configuracao de
/// autorizacao. Nao existe caminho em que uma API key vire sessao humana.
///
/// A validacao tem quatro etapas, e todas precisam passar:
/// 1. o formato da chave;
/// 2. o segredo confere com o hash da linha;
/// 3. a credencial nao foi revogada;
/// 4. a integracao e a organizacao continuam ativas.
///
/// A etapa 4 e o que faz "desativar a integracao" ter efeito imediato, em vez
/// de esperar alguem revogar cada credencial.
/// </summary>
public sealed partial class ManipuladorDeAutenticacaoDeIntegracao
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string Esquema = "Integracao";

    /// <summary>Prefixo do cabecalho Authorization.</summary>
    public const string PrefixoDoEsquema = "ApiKey ";

    /// <summary>Claim com a organizacao. Mesmo nome usado pelo token humano.</summary>
    public const string ClaimDeOrganizacao = "org";

    /// <summary>Claim com a integracao autenticada.</summary>
    public const string ClaimDeIntegracao = "integracao";

    private readonly IProtetorDeCredencial _protetor;
    private readonly IRepositorioDeIntegracoes _integracoes;
    private readonly IRelogio _relogio;

    public ManipuladorDeAutenticacaoDeIntegracao(
        IOptionsMonitor<AuthenticationSchemeOptions> opcoes,
        ILoggerFactory log,
        UrlEncoder codificador,
        IProtetorDeCredencial protetor,
        IRepositorioDeIntegracoes integracoes,
        IRelogio relogio)
        : base(opcoes, log, codificador)
    {
        _protetor = protetor;
        _integracoes = integracoes;
        _relogio = relogio;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cabecalho = Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(cabecalho) ||
            !cabecalho.StartsWith(PrefixoDoEsquema, StringComparison.Ordinal))
        {
            // NoResult, e nao Fail: a requisicao simplesmente nao se
            // apresentou com este esquema. Falhar aqui atrapalharia uma
            // eventual composicao com outro esquema.
            return AuthenticateResult.NoResult();
        }

        var chave = cabecalho[PrefixoDoEsquema.Length..];

        if (!_protetor.TentarInterpretar(chave, out var identificadorPublico, out var hashDoSegredo))
        {
            return Recusar("formato invalido");
        }

        var credencial = await _integracoes.BuscarCredencialPorIdentificadorPublicoIgnorandoTenantAsync(
            identificadorPublico,
            Context.RequestAborted);

        // A comparacao e entre hashes de 256 bits sorteados, nao entre
        // segredos de baixa entropia: nao ha o que um ataque de tempo revele
        // aqui. Ainda assim a resposta e identica em todos os ramos de falha.
        if (credencial is null)
        {
            return Recusar($"credencial {identificadorPublico} nao encontrada");
        }

        if (!string.Equals(credencial.HashDoSegredo, hashDoSegredo, StringComparison.Ordinal))
        {
            return Recusar($"segredo nao confere para {identificadorPublico}");
        }

        if (credencial.EstaRevogada)
        {
            return Recusar($"credencial {identificadorPublico} revogada");
        }

        var contexto = await _integracoes.BuscarContextoDaCredencialIgnorandoTenantAsync(
            credencial.IntegracaoId,
            Context.RequestAborted);

        if (contexto is not { Integracao.Ativa: true, OrganizacaoAtiva: true })
        {
            return Recusar($"integracao {credencial.IntegracaoId} inativa ou organizacao suspensa");
        }

        // Carimba o uso, o que permite responder "posso revogar esta
        // credencial antiga?" sem palpite. Grava no maximo uma vez por
        // minuto, entao nao vira um UPDATE por requisicao.
        if (credencial.RegistrarUso(_relogio.Agora))
        {
            await Context.RequestServices
                .GetRequiredService<Application.Identidade.IUnidadeDeTrabalho>()
                .SalvarAsync(Context.RequestAborted);
        }

        var identidade = new ClaimsIdentity(
            [
                new Claim(ClaimDeOrganizacao, credencial.OrganizacaoId.ToString()),
                new Claim(ClaimDeIntegracao, credencial.IntegracaoId.ToString()),
            ],
            Esquema);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identidade), Esquema));
    }

    /// <summary>
    /// Uma unica mensagem para toda falha: chave malformada, inexistente,
    /// segredo errado, credencial revogada, integracao desativada e
    /// organizacao suspensa sao indistinguiveis de fora.
    ///
    /// Distinguir daria a quem testa chaves um mapa do que existe.
    /// </summary>
    private AuthenticateResult Recusar(string motivo)
    {
        // O motivo vai para o log, NUNCA para a resposta. E o identificador
        // publico da credencial - nunca o segredo - que aparece aqui, para que
        // o suporte consiga investigar sem que o log vire um vazamento.
        RegistrarRecusa(Logger, motivo);

        return AuthenticateResult.Fail("Credencial de integracao invalida.");
    }

    [LoggerMessage(
        EventId = 200,
        Level = LogLevel.Debug,
        Message = "Ingestao recusada: {Motivo}.")]
    private static partial void RegistrarRecusa(ILogger logger, string motivo);
}
