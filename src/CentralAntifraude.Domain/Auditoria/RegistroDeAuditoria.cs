using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Auditoria;

/// <summary>
/// Operacoes que deixam rastro permanente.
///
/// Vocabulario fechado de proposito (CLAUDE.md secao 67): auditoria com
/// texto livre nao e consultavel nem comparavel, e a Fase 10 precisa filtrar
/// por operacao.
/// </summary>
public enum OperacaoAuditada
{
    LoginBemSucedido = 1,
    LoginRecusado = 2,
    Logout = 3,
    ReusoDeRefreshTokenDetectado = 4,
    UsuarioCriado = 5,
    UsuarioAlterado = 6,
    PerfilDeUsuarioAlterado = 7,
    UsuarioDesativado = 8,
    UsuarioReativado = 9,
    SenhaDeUsuarioAlterada = 10,

    // Integracoes (Fase 2)
    IntegracaoCriada = 20,
    IntegracaoAlterada = 21,
    IntegracaoDesativada = 22,
    IntegracaoReativada = 23,
    CredencialDeIntegracaoEmitida = 24,
    CredencialDeIntegracaoRevogada = 25,
    IngestaoRecusadaPorCredencial = 26,

    // Investigacao (Fase 7)
    CasoAberto = 40,
    CasoAlterado = 41,
    CasoAtribuido = 42,
    CasoResolvido = 43,

    // Regras e perfil de risco (Fase 8)
    RegraCriada = 60,
    RascunhoDeRegraSalvo = 61,
    RascunhoDeRegraDescartado = 62,
    VersaoDeRegraPublicada = 63,
    RegraDesativada = 64,
    RegraReativada = 65,
    VersaoDePerfilPublicada = 66,
}

/// <summary>
/// Trilha somente-insercao das operacoes sensiveis.
///
/// Nao existe metodo para alterar nem apagar um registro, e nao ha endpoint
/// que faca isso. Uma trilha que pode ser editada nao prova nada: seria
/// exatamente o registro que um invasor apagaria primeiro.
///
/// O que NAO entra aqui: senha, hash de senha, token, valor de refresh
/// token, cabecalho de autorizacao. A trilha responde "quem fez o que e
/// quando", nao "com qual credencial".
/// </summary>
public sealed class RegistroDeAuditoria
{
    public const int TamanhoMaximoDoDetalhe = 2_000;

    private RegistroDeAuditoria(
        Guid id,
        Guid organizacaoId,
        OperacaoAuditada operacao,
        Guid? autorId,
        string? autorDescricao,
        string entidade,
        string? entidadeId,
        string? detalhe,
        string? idDeCorrelacao,
        DateTimeOffset ocorridoEm)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        Operacao = operacao;
        AutorId = autorId;
        AutorDescricao = autorDescricao;
        Entidade = entidade;
        EntidadeId = entidadeId;
        Detalhe = detalhe;
        IdDeCorrelacao = idDeCorrelacao;
        OcorridoEm = ocorridoEm;
    }

    // Construtor usado pelo EF Core na materializacao.
    private RegistroDeAuditoria() => Entidade = string.Empty;

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public OperacaoAuditada Operacao { get; private set; }

    /// <summary>
    /// Quem executou. Nulo quando a operacao nao tem autor identificado —
    /// uma tentativa de login recusada, por exemplo.
    /// </summary>
    public Guid? AutorId { get; private set; }

    /// <summary>
    /// Identificacao legivel do autor no momento do fato. Guardada por copia
    /// e nao por join: se o usuario for renomeado depois, a trilha precisa
    /// continuar dizendo quem era quando aquilo aconteceu.
    /// </summary>
    public string? AutorDescricao { get; private set; }

    /// <summary>Tipo do recurso afetado. Ex.: "Usuario".</summary>
    public string Entidade { get; private set; }

    public string? EntidadeId { get; private set; }

    /// <summary>Contexto curto e seguro. Ex.: "Perfil: Auditor -> Analista".</summary>
    public string? Detalhe { get; private set; }

    /// <summary>Liga o registro a requisicao HTTP que o originou.</summary>
    public string? IdDeCorrelacao { get; private set; }

    public DateTimeOffset OcorridoEm { get; private set; }

    public static RegistroDeAuditoria Registrar(
        Guid organizacaoId,
        OperacaoAuditada operacao,
        string entidade,
        DateTimeOffset agora,
        Guid? autorId = null,
        string? autorDescricao = null,
        string? entidadeId = null,
        string? detalhe = null,
        string? idDeCorrelacao = null)
    {
        if (organizacaoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Registro de auditoria exige organizacao.");
        }

        if (!Enum.IsDefined(operacao))
        {
            throw new ViolacaoDeInvariante("Operacao auditada desconhecida.");
        }

        if (string.IsNullOrWhiteSpace(entidade))
        {
            throw new ViolacaoDeInvariante("Registro de auditoria exige a entidade afetada.");
        }

        return new RegistroDeAuditoria(
            Identificador.Novo(),
            organizacaoId,
            operacao,
            autorId,
            Encurtar(autorDescricao, 200),
            entidade.Trim(),
            entidadeId,
            Encurtar(detalhe, TamanhoMaximoDoDetalhe),
            Encurtar(idDeCorrelacao, 64),
            agora);
    }

    /// <summary>
    /// Corta em vez de recusar: perder o fim de um detalhe e ruim, mas
    /// perder o registro inteiro porque alguem mandou um texto grande e pior.
    /// </summary>
    private static string? Encurtar(string? valor, int tamanhoMaximo)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return null;
        }

        var limpo = valor.Trim();

        return limpo.Length <= tamanhoMaximo ? limpo : limpo[..tamanhoMaximo];
    }
}
