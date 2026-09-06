using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Risco;

/// <summary>
/// O perfil de risco de uma organizacao: quais regras valem e onde ficam os
/// limiares de decisao.
///
/// Como a regra, o perfil e a identidade estavel; o que muda sao as versoes.
/// </summary>
public sealed class PerfilDeRisco
{
    public const int TamanhoMaximoDoNome = 120;

    private PerfilDeRisco(Guid id, Guid organizacaoId, string nome, DateTimeOffset agora)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        Nome = nome;
        CriadoEm = agora;
    }

    // Construtor usado pelo EF Core na materializacao.
    private PerfilDeRisco() => Nome = string.Empty;

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public string Nome { get; private set; }

    public DateTimeOffset CriadoEm { get; private set; }

    public static PerfilDeRisco Criar(Guid organizacaoId, string nome, DateTimeOffset agora)
    {
        if (organizacaoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Perfil de risco precisa pertencer a uma organizacao.");
        }

        if (string.IsNullOrWhiteSpace(nome) || nome.Trim().Length > TamanhoMaximoDoNome)
        {
            throw new ViolacaoDeInvariante(
                $"Nome do perfil e obrigatorio e deve ter no maximo {TamanhoMaximoDoNome} caracteres.");
        }

        return new PerfilDeRisco(Identificador.Novo(), organizacaoId, nome.Trim(), agora);
    }
}

/// <summary>
/// Uma versao publicada do perfil: o conjunto de versoes de regra e os dois
/// limiares, congelados.
///
/// **Imutavel apos a publicacao.** Publicar uma versao nova nao altera as
/// avaliacoes anteriores — elas continuam apontando para a versao que valia
/// quando foram feitas (CLAUDE.md secao 24).
///
/// Os limiares sao dois porque as decisoes sao tres:
/// <code>
/// score &lt; LimiarDeRevisao                              -> Permitir
/// LimiarDeRevisao &lt;= score &lt; LimiarDeBloqueio        -> Revisar
/// score &gt;= LimiarDeBloqueio                            -> Bloquear
/// </code>
/// </summary>
public sealed class VersaoDePerfilDeRisco
{
    public const int ScoreMinimo = 0;
    public const int ScoreMaximo = 100;

    private readonly List<VersaoDeRegra> _versoesDeRegra = [];

    private VersaoDePerfilDeRisco(
        Guid id,
        Guid organizacaoId,
        Guid perfilId,
        int numero,
        int limiarDeRevisao,
        int limiarDeBloqueio,
        DateTimeOffset publicadaEm)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        PerfilId = perfilId;
        Numero = numero;
        LimiarDeRevisao = limiarDeRevisao;
        LimiarDeBloqueio = limiarDeBloqueio;
        PublicadaEm = publicadaEm;
    }

    // Construtor usado pelo EF Core na materializacao.
    private VersaoDePerfilDeRisco()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public Guid PerfilId { get; private set; }

    public int Numero { get; private set; }

    /// <summary>A partir daqui, a recomendacao e revisar.</summary>
    public int LimiarDeRevisao { get; private set; }

    /// <summary>A partir daqui, a recomendacao e bloquear.</summary>
    public int LimiarDeBloqueio { get; private set; }

    public DateTimeOffset PublicadaEm { get; private set; }

    /// <summary>Versoes de regra que compoem este perfil.</summary>
    public IReadOnlyList<VersaoDeRegra> VersoesDeRegra => _versoesDeRegra;

    public static VersaoDePerfilDeRisco Publicar(
        PerfilDeRisco perfil,
        int numero,
        int limiarDeRevisao,
        int limiarDeBloqueio,
        IReadOnlyList<VersaoDeRegra> versoesDeRegra,
        DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(perfil);
        ArgumentNullException.ThrowIfNull(versoesDeRegra);

        if (numero < 1)
        {
            throw new ViolacaoDeInvariante("Numero da versao comeca em 1.");
        }

        ValidarLimiares(limiarDeRevisao, limiarDeBloqueio);

        // Um perfil sem regra nenhuma pontuaria tudo com zero e recomendaria
        // Permitir para qualquer transacao. Nao daria erro, nao apareceria em
        // log nenhum: o motor simplesmente ficaria cego. Desativar a ultima
        // regra ativa e recusado por causa desta invariante.
        if (versoesDeRegra.Count == 0)
        {
            throw new ViolacaoDeInvariante(
                "Um perfil publicado precisa de ao menos uma regra. Sem regra, " +
                "toda transacao receberia score zero e o motor ficaria cego.");
        }

        if (versoesDeRegra.Any(v => v.OrganizacaoId != perfil.OrganizacaoId))
        {
            throw new ViolacaoDeInvariante(
                "Um perfil nao pode incluir versao de regra de outra organizacao.");
        }

        // Duas versoes da mesma regra no mesmo perfil somariam pontos duas
        // vezes pelo mesmo motivo, com configuracoes possivelmente
        // contraditorias.
        if (versoesDeRegra.Select(v => v.RegraId).Distinct().Count() != versoesDeRegra.Count)
        {
            throw new ViolacaoDeInvariante("O perfil nao pode conter duas versoes da mesma regra.");
        }

        var versao = new VersaoDePerfilDeRisco(
            Identificador.Novo(),
            perfil.OrganizacaoId,
            perfil.Id,
            numero,
            limiarDeRevisao,
            limiarDeBloqueio,
            agora);

        versao._versoesDeRegra.AddRange(versoesDeRegra);

        return versao;
    }

    private static void ValidarLimiares(int limiarDeRevisao, int limiarDeBloqueio)
    {
        if (limiarDeRevisao is < ScoreMinimo or > ScoreMaximo ||
            limiarDeBloqueio is < ScoreMinimo or > ScoreMaximo)
        {
            throw new ViolacaoDeInvariante(
                $"Limiares devem estar entre {ScoreMinimo} e {ScoreMaximo}.");
        }

        // Limiares invertidos ou iguais tornariam uma das tres decisoes
        // inalcancavel - o perfil teria uma faixa que nunca acontece, e
        // ninguem perceberia ate investigar por que nada e revisado.
        if (limiarDeRevisao >= limiarDeBloqueio)
        {
            throw new ViolacaoDeInvariante(
                "LimiarDeRevisao deve ser menor que LimiarDeBloqueio, " +
                "senao a faixa de revisao fica vazia.");
        }

        if (limiarDeRevisao == ScoreMinimo)
        {
            throw new ViolacaoDeInvariante(
                "LimiarDeRevisao igual a zero tornaria a decisao Permitir inalcancavel.");
        }
    }

    /// <summary>
    /// Traduz o score na recomendacao, pelos limiares desta versao.
    ///
    /// Fica no perfil, e nao no motor, porque e o perfil que responde por essa
    /// escolha — e e a versao do perfil que a avaliacao guarda para poder
    /// explicar a decisao depois.
    /// </summary>
    public Decisao DecidirPor(int score)
    {
        if (score is < ScoreMinimo or > ScoreMaximo)
        {
            throw new ViolacaoDeInvariante(
                $"Score fora da faixa {ScoreMinimo}..{ScoreMaximo}: {score}.");
        }

        if (score >= LimiarDeBloqueio)
        {
            return Decisao.Bloquear;
        }

        return score >= LimiarDeRevisao ? Decisao.Revisar : Decisao.Permitir;
    }
}
