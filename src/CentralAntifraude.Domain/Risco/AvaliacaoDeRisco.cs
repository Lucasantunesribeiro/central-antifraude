using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Risco;

/// <summary>
/// Uma evidencia produzida por uma regra durante uma avaliacao.
///
/// O sinal guarda a explicacao **por copia**, e nao por referencia a regra.
/// A frase foi montada com a configuracao que valia naquele instante; se ela
/// fosse remontada na hora de exibir, uma mudanca de configuracao reescreveria
/// o passado (ROADMAP secao 3.6).
/// </summary>
public sealed class SinalDeRisco
{
    public const int TamanhoMaximoDaExplicacao = 500;

    private SinalDeRisco(
        Guid id,
        Guid organizacaoId,
        Guid avaliacaoId,
        TipoDeRegra tipo,
        Guid regraId,
        Guid versaoDeRegraId,
        int numeroDaVersaoDeRegra,
        int pontos,
        string explicacao,
        IReadOnlyDictionary<string, string> dadosDaEvidencia)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        AvaliacaoId = avaliacaoId;
        Tipo = tipo;
        RegraId = regraId;
        VersaoDeRegraId = versaoDeRegraId;
        NumeroDaVersaoDeRegra = numeroDaVersaoDeRegra;
        Pontos = pontos;
        Explicacao = explicacao;
        DadosDaEvidencia = dadosDaEvidencia;
    }

    // Construtor usado pelo EF Core na materializacao.
    private SinalDeRisco()
    {
        Explicacao = string.Empty;
        DadosDaEvidencia = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public Guid AvaliacaoId { get; private set; }

    public TipoDeRegra Tipo { get; private set; }

    public Guid RegraId { get; private set; }

    /// <summary>Versao exata da regra que produziu este sinal.</summary>
    public Guid VersaoDeRegraId { get; private set; }

    public int NumeroDaVersaoDeRegra { get; private set; }

    /// <summary>Quanto este sinal somou ao score.</summary>
    public int Pontos { get; private set; }

    /// <summary>Frase deterministica, montada no momento da avaliacao.</summary>
    public string Explicacao { get; private set; }

    /// <summary>Numeros que sustentam a explicacao, em pares nome/valor.</summary>
    public IReadOnlyDictionary<string, string> DadosDaEvidencia { get; private set; }

    internal static SinalDeRisco Registrar(
        Guid organizacaoId,
        Guid avaliacaoId,
        VersaoDeRegra versaoDeRegra,
        SinalCalculado calculado)
    {
        ArgumentNullException.ThrowIfNull(versaoDeRegra);
        ArgumentNullException.ThrowIfNull(calculado);

        var explicacao = calculado.Explicacao.Length <= TamanhoMaximoDaExplicacao
            ? calculado.Explicacao
            : calculado.Explicacao[..TamanhoMaximoDaExplicacao];

        return new SinalDeRisco(
            Identificador.Novo(),
            organizacaoId,
            avaliacaoId,
            versaoDeRegra.Tipo,
            versaoDeRegra.RegraId,
            versaoDeRegra.Id,
            versaoDeRegra.Numero,
            versaoDeRegra.Pontos,
            explicacao,
            calculado.DadosDaEvidencia);
    }
}

/// <summary>
/// O resultado da avaliacao de risco de uma transacao.
///
/// Responde a uma pergunta so, e precisa continuar respondendo anos depois:
///
/// > **Por que esta decisao foi tomada naquele momento?**
///
/// Por isso guarda, congelados: a versao do perfil usada, a versao do motor,
/// o score, a decisao e os sinais com suas explicacoes. Nada disso e
/// recalculado na hora de exibir — publicar uma regra nova em setembro nao
/// pode mudar o que uma avaliacao de marco diz (CLAUDE.md secao 17).
/// </summary>
public sealed class AvaliacaoDeRisco
{
    /// <summary>
    /// Versao da semantica do motor.
    ///
    /// Muda quando a forma de calcular muda — nao quando uma regra e
    /// reconfigurada, que ja e coberto pela versao da regra. Guardada na
    /// avaliacao para que uma diferenca de resultado entre duas epocas possa
    /// ser atribuida ao motor ou a configuracao, e nao a um mistério.
    /// </summary>
    public const string VersaoDoMotor = "1.0";

    private readonly List<SinalDeRisco> _sinais = [];

    private AvaliacaoDeRisco(
        Guid id,
        Guid organizacaoId,
        Guid transacaoId,
        Guid versaoDePerfilId,
        int numeroDaVersaoDePerfil,
        int score,
        Decisao decisao,
        string versaoDoMotor,
        DateTimeOffset avaliadaEm)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        TransacaoId = transacaoId;
        VersaoDePerfilId = versaoDePerfilId;
        NumeroDaVersaoDePerfil = numeroDaVersaoDePerfil;
        Score = score;
        Decisao = decisao;
        VersaoDoMotorUsada = versaoDoMotor;
        AvaliadaEm = avaliadaEm;
    }

    // Construtor usado pelo EF Core na materializacao.
    private AvaliacaoDeRisco() => VersaoDoMotorUsada = string.Empty;

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public Guid TransacaoId { get; private set; }

    /// <summary>Versao exata do perfil que decidiu. Nao muda com publicacoes futuras.</summary>
    public Guid VersaoDePerfilId { get; private set; }

    public int NumeroDaVersaoDePerfil { get; private set; }

    /// <summary>Score final, sempre entre 0 e 100.</summary>
    public int Score { get; private set; }

    public Decisao Decisao { get; private set; }

    public string VersaoDoMotorUsada { get; private set; }

    /// <summary>Terceiro tempo da transacao, distinto de OcorridaEm e RecebidaEm.</summary>
    public DateTimeOffset AvaliadaEm { get; private set; }

    public IReadOnlyList<SinalDeRisco> Sinais => _sinais;

    /// <summary>
    /// Monta a avaliacao a partir do que o motor calculou.
    ///
    /// Interno de proposito: quem cria avaliacao e o motor. Nao ha caminho
    /// pelo qual a borda HTTP construa uma avaliacao com score escolhido a
    /// dedo (Security Gate 3).
    /// </summary>
    internal static AvaliacaoDeRisco Registrar(
        Guid transacaoId,
        VersaoDePerfilDeRisco versaoDoPerfil,
        IReadOnlyList<(VersaoDeRegra Versao, SinalCalculado Calculado)> sinaisCalculados,
        DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(versaoDoPerfil);
        ArgumentNullException.ThrowIfNull(sinaisCalculados);

        if (transacaoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Avaliacao precisa apontar para uma transacao.");
        }

        // O score e a soma das contribuicoes, limitada a 100. O teto e do
        // contrato do produto (CLAUDE.md secao 20): a faixa 0..100 e o que
        // torna dois perfis comparaveis e o que os limiares assumem.
        var bruto = sinaisCalculados.Sum(s => s.Versao.Pontos);
        var score = Math.Clamp(bruto, VersaoDePerfilDeRisco.ScoreMinimo, VersaoDePerfilDeRisco.ScoreMaximo);

        var avaliacao = new AvaliacaoDeRisco(
            Identificador.Novo(),
            versaoDoPerfil.OrganizacaoId,
            transacaoId,
            versaoDoPerfil.Id,
            versaoDoPerfil.Numero,
            score,
            versaoDoPerfil.DecidirPor(score),
            VersaoDoMotor,
            agora);

        foreach (var (versao, calculado) in sinaisCalculados)
        {
            avaliacao._sinais.Add(
                SinalDeRisco.Registrar(versaoDoPerfil.OrganizacaoId, avaliacao.Id, versao, calculado));
        }

        return avaliacao;
    }

    /// <summary>
    /// Soma dos pontos antes do teto de 100.
    ///
    /// Util para explicar por que dois casos diferentes receberam o mesmo
    /// score: um pode ter somado 100 e o outro 180.
    /// </summary>
    public int SomaBrutaDosPontos => _sinais.Sum(s => s.Pontos);

    /// <summary>Verdadeiro quando o teto de 100 cortou a soma.</summary>
    public bool ScoreFoiLimitado => SomaBrutaDosPontos > Score;
}
