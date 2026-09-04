using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Integracoes;

public enum MotivoDeRevogacaoDeCredencial
{
    /// <summary>Revogada manualmente por um administrador.</summary>
    RevogadaManualmente = 1,

    /// <summary>Substituida por uma credencial nova durante a rotacao.</summary>
    Rotacionada = 2,

    /// <summary>A integracao inteira foi desativada.</summary>
    IntegracaoDesativada = 3,
}

/// <summary>
/// Credencial de uma integracao — a "API key".
///
/// **Por que uma entidade separada, e nao um campo na integracao.** Rotacao
/// sem interrupcao exige que duas credenciais valham ao mesmo tempo: a nova e
/// distribuida, o integrador troca a configuracao dele quando puder, e so
/// entao a antiga e revogada. Com um campo unico, toda rotacao derrubaria a
/// ingestao do cliente no instante em que fosse feita.
///
/// **O valor bruto existe uma vez.** Ele e devolvido na resposta da criacao e
/// nunca mais — o que fica no banco e o hash. Um vazamento do banco nao
/// entrega credencial a ninguem.
///
/// **Formato:** <c>caf_{identificadorPublico}_{segredo}</c>
/// - o prefixo <c>caf_</c> e fixo e serve para que varredores de segredo
///   (gitleaks e afins) reconhecam a chave se ela vazar em um repositorio;
/// - o identificador publico permite localizar a linha por indice, em vez de
///   comparar o hash contra todas as credenciais do banco;
/// - o segredo tem 256 bits de aleatoriedade criptografica.
/// </summary>
public sealed class CredencialDeIntegracao
{
    /// <summary>Prefixo fixo, reconhecivel por varredor de segredos.</summary>
    public const string Prefixo = "caf";

    /// <summary>Caracteres do identificador publico (8 bytes em hexadecimal).</summary>
    public const int TamanhoDoIdentificadorPublico = 16;

    private CredencialDeIntegracao(
        Guid id,
        Guid organizacaoId,
        Guid integracaoId,
        string identificadorPublico,
        string hashDoSegredo,
        DateTimeOffset agora)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        IntegracaoId = integracaoId;
        IdentificadorPublico = identificadorPublico;
        HashDoSegredo = hashDoSegredo;
        CriadaEm = agora;
    }

    // Construtor usado pelo EF Core na materializacao.
    private CredencialDeIntegracao()
    {
        IdentificadorPublico = string.Empty;
        HashDoSegredo = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public Guid IntegracaoId { get; private set; }

    /// <summary>
    /// Parte publica da chave, usada apenas para localizar a linha.
    /// Nao e segredo: conhecer este valor nao permite autenticar.
    /// </summary>
    public string IdentificadorPublico { get; private set; }

    /// <summary>SHA-256 do segredo, em hexadecimal minusculo.</summary>
    public string HashDoSegredo { get; private set; }

    public DateTimeOffset CriadaEm { get; private set; }

    /// <summary>
    /// Ultima vez que esta credencial autenticou com sucesso.
    ///
    /// Serve a uma pergunta operacional concreta: "posso revogar esta
    /// credencial antiga com seguranca?". Sem o carimbo, a resposta seria um
    /// palpite.
    /// </summary>
    public DateTimeOffset? UsadaPelaUltimaVezEm { get; private set; }

    public DateTimeOffset? RevogadaEm { get; private set; }

    public MotivoDeRevogacaoDeCredencial? MotivoDaRevogacao { get; private set; }

    public bool EstaRevogada => RevogadaEm is not null;

    public static CredencialDeIntegracao Criar(
        Guid organizacaoId,
        Guid integracaoId,
        string identificadorPublico,
        string hashDoSegredo,
        DateTimeOffset agora)
    {
        if (organizacaoId == Guid.Empty || integracaoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Credencial exige organizacao e integracao.");
        }

        if (string.IsNullOrWhiteSpace(identificadorPublico) || string.IsNullOrWhiteSpace(hashDoSegredo))
        {
            throw new ViolacaoDeInvariante("Credencial exige identificador publico e hash do segredo.");
        }

        return new CredencialDeIntegracao(
            Identificador.Novo(),
            organizacaoId,
            integracaoId,
            identificadorPublico,
            hashDoSegredo,
            agora);
    }

    /// <summary>
    /// Registra o uso. Grava com granularidade de minuto: a informacao serve
    /// para decidir sobre revogacao, e um UPDATE por requisicao no caminho
    /// critico de ingestao seria custo puro.
    /// </summary>
    public bool RegistrarUso(DateTimeOffset agora)
    {
        if (UsadaPelaUltimaVezEm is { } anterior &&
            agora - anterior < TimeSpan.FromMinutes(1))
        {
            return false;
        }

        UsadaPelaUltimaVezEm = agora;
        return true;
    }

    /// <summary>
    /// Revoga a credencial. Idempotente: revogar de novo nao reescreve o
    /// motivo original, que e o que explica o que aconteceu.
    /// </summary>
    public void Revogar(MotivoDeRevogacaoDeCredencial motivo, DateTimeOffset agora)
    {
        if (EstaRevogada)
        {
            return;
        }

        RevogadaEm = agora;
        MotivoDaRevogacao = motivo;
    }
}
