using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Identidade;

/// <summary>Por que um refresh token deixou de valer.</summary>
public enum MotivoDeRevogacao
{
    /// <summary>Uso normal: foi trocado por outro na rotacao.</summary>
    Rotacionado = 1,

    /// <summary>O usuario encerrou a sessao.</summary>
    Logout = 2,

    /// <summary>
    /// Um token ja usado foi apresentado de novo. Sinal de vazamento:
    /// toda a familia cai junto.
    /// </summary>
    ReusoDetectado = 3,

    /// <summary>Usuario ou organizacao foi desativado.</summary>
    AcessoRevogado = 4,
}

/// <summary>
/// Refresh token de uma sessao humana.
///
/// Tres decisoes moram aqui, e cada uma resolve um problema concreto:
///
/// 1. **So o hash e persistido.** O valor bruto existe uma vez, na resposta
///    HTTP, e some. Um vazamento de banco nao entrega sessao a ninguem.
///
/// 2. **Rotacao.** Cada uso troca o token por um novo. A janela em que um
///    token roubado serve deixa de ser "ate expirar" e passa a ser "ate o
///    dono usar o proximo".
///
/// 3. **Familia.** Todos os tokens descendentes de um mesmo login
///    compartilham <see cref="FamiliaId"/>. Se um token ja usado reaparecer,
///    ha duas copias em circulacao — o legitimo e o roubado — e nao da para
///    saber qual e qual. A resposta correta e derrubar a familia inteira e
///    obrigar novo login.
///
/// Nao ha entidade "Sessao" separada: a familia ja e a sessao, e uma tabela
/// a mais nao acrescentaria capacidade nenhuma.
/// </summary>
public sealed class RefreshToken
{
    private RefreshToken(
        Guid id,
        Guid organizacaoId,
        Guid usuarioId,
        Guid familiaId,
        string hashDoToken,
        DateTimeOffset criadoEm,
        DateTimeOffset expiraEm)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        UsuarioId = usuarioId;
        FamiliaId = familiaId;
        HashDoToken = hashDoToken;
        CriadoEm = criadoEm;
        ExpiraEm = expiraEm;
    }

    // Construtor usado pelo EF Core na materializacao.
    private RefreshToken() => HashDoToken = string.Empty;

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public Guid UsuarioId { get; private set; }

    /// <summary>Liga todos os tokens originados do mesmo login.</summary>
    public Guid FamiliaId { get; private set; }

    /// <summary>SHA-256 do token bruto, em hexadecimal minusculo.</summary>
    public string HashDoToken { get; private set; }

    public DateTimeOffset CriadoEm { get; private set; }

    public DateTimeOffset ExpiraEm { get; private set; }

    /// <summary>Quando foi trocado por outro. Nulo enquanto nao usado.</summary>
    public DateTimeOffset? UsadoEm { get; private set; }

    public DateTimeOffset? RevogadoEm { get; private set; }

    public MotivoDeRevogacao? MotivoDaRevogacao { get; private set; }

    public bool EstaRevogado => RevogadoEm is not null;

    public bool JaFoiUsado => UsadoEm is not null;

    /// <summary>
    /// Primeiro token de uma familia nova. Um login sempre comeca uma familia.
    /// </summary>
    public static RefreshToken IniciarFamilia(
        Guid organizacaoId,
        Guid usuarioId,
        string hashDoToken,
        DateTimeOffset agora,
        TimeSpan validade) =>
        Criar(organizacaoId, usuarioId, Identificador.Novo(), hashDoToken, agora, validade);

    /// <summary>Proximo token da mesma familia, emitido na rotacao.</summary>
    public RefreshToken Suceder(string hashDoNovoToken, DateTimeOffset agora, TimeSpan validade) =>
        Criar(OrganizacaoId, UsuarioId, FamiliaId, hashDoNovoToken, agora, validade);

    private static RefreshToken Criar(
        Guid organizacaoId,
        Guid usuarioId,
        Guid familiaId,
        string hashDoToken,
        DateTimeOffset agora,
        TimeSpan validade)
    {
        if (organizacaoId == Guid.Empty || usuarioId == Guid.Empty || familiaId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Refresh token exige organizacao, usuario e familia.");
        }

        if (string.IsNullOrWhiteSpace(hashDoToken))
        {
            throw new ViolacaoDeInvariante("Refresh token exige o hash do valor emitido.");
        }

        if (validade <= TimeSpan.Zero)
        {
            throw new ViolacaoDeInvariante("Validade do refresh token deve ser positiva.");
        }

        return new RefreshToken(
            Identificador.Novo(),
            organizacaoId,
            usuarioId,
            familiaId,
            hashDoToken,
            agora,
            agora + validade);
    }

    /// <summary>
    /// Um token so pode ser trocado se nunca foi usado, nao foi revogado e
    /// nao expirou. As tres condicoes sao verificadas juntas de proposito:
    /// separa-las convidaria a esquecer uma.
    /// </summary>
    public bool PodeSerUsado(DateTimeOffset agora) =>
        !JaFoiUsado && !EstaRevogado && agora < ExpiraEm;

    /// <summary>Marca o token como trocado por outro.</summary>
    public void MarcarComoUsado(DateTimeOffset agora)
    {
        if (JaFoiUsado)
        {
            throw new ViolacaoDeInvariante("Refresh token ja foi usado.");
        }

        UsadoEm = agora;
        RevogadoEm = agora;
        MotivoDaRevogacao = MotivoDeRevogacao.Rotacionado;
    }

    /// <summary>
    /// Revoga o token. Idempotente: revogar de novo nao reescreve o motivo
    /// original, porque o primeiro motivo e o que explica o que aconteceu.
    /// </summary>
    public void Revogar(MotivoDeRevogacao motivo, DateTimeOffset agora)
    {
        if (EstaRevogado)
        {
            return;
        }

        RevogadoEm = agora;
        MotivoDaRevogacao = motivo;
    }
}
