using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Risco;

/// <summary>
/// Uma regra de risco da organizacao.
///
/// A regra e a identidade estavel — "velocidade por cliente do tenant X". O
/// que muda com o tempo sao as <see cref="VersaoDeRegra"/>: cada ajuste de
/// configuracao ou de peso cria uma versao nova, e a anterior continua
/// existindo para explicar as avaliacoes que a usaram.
///
/// A administracao completa (rascunho, publicacao, substituicao) e a Fase 8.
/// Aqui as regras vem do catalogo padrao provisionado junto da organizacao.
/// </summary>
public sealed class Regra
{
    public const int TamanhoMaximoDoNome = 120;

    private Regra(Guid id, Guid organizacaoId, TipoDeRegra tipo, string nome, DateTimeOffset agora)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        Tipo = tipo;
        Nome = nome;
        CriadaEm = agora;
    }

    // Construtor usado pelo EF Core na materializacao.
    private Regra() => Nome = string.Empty;

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public TipoDeRegra Tipo { get; private set; }

    public string Nome { get; private set; }

    public DateTimeOffset CriadaEm { get; private set; }

    public static Regra Criar(Guid organizacaoId, TipoDeRegra tipo, string nome, DateTimeOffset agora)
    {
        if (organizacaoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Regra precisa pertencer a uma organizacao.");
        }

        if (!Enum.IsDefined(tipo))
        {
            throw new ViolacaoDeInvariante("Tipo de regra fora do catalogo.");
        }

        if (string.IsNullOrWhiteSpace(nome) || nome.Trim().Length > TamanhoMaximoDoNome)
        {
            throw new ViolacaoDeInvariante(
                $"Nome da regra e obrigatorio e deve ter no maximo {TamanhoMaximoDoNome} caracteres.");
        }

        return new Regra(Identificador.Novo(), organizacaoId, tipo, nome.Trim(), agora);
    }
}

/// <summary>
/// Uma versao publicada de uma regra: a configuracao e o peso congelados.
///
/// **Imutavel apos a publicacao** (CLAUDE.md secao 23). Nao ha metodo que
/// altere configuracao ou pontos — nao por convencao, mas porque nao existe.
///
/// Isso e o que sustenta a explicabilidade historica: uma avaliacao de marco
/// aponta para a versao que valia em marco, e essa versao nao muda quando
/// alguem ajusta a regra em setembro.
/// </summary>
public sealed class VersaoDeRegra
{
    /// <summary>Teto de pontos de um unico sinal, alinhado a faixa 0..100 do score.</summary>
    public const int PontosMaximos = 100;

    private VersaoDeRegra(
        Guid id,
        Guid organizacaoId,
        Guid regraId,
        int numero,
        TipoDeRegra tipo,
        ConfiguracaoDeRegra configuracao,
        int pontos,
        DateTimeOffset publicadaEm)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        RegraId = regraId;
        Numero = numero;
        Tipo = tipo;
        Configuracao = configuracao;
        Pontos = pontos;
        PublicadaEm = publicadaEm;
    }

    // Construtor usado pelo EF Core na materializacao.
    private VersaoDeRegra() => Configuracao = null!;

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public Guid RegraId { get; private set; }

    /// <summary>Numero sequencial da versao dentro da regra: 1, 2, 3...</summary>
    public int Numero { get; private set; }

    /// <summary>
    /// Copia do tipo da regra.
    ///
    /// Redundante de proposito: a avaliacao guarda a versao, e ler o tipo dali
    /// evita um join so para descobrir de que regra o sinal veio. O tipo de
    /// uma regra nao muda — se mudasse, seria outra regra.
    /// </summary>
    public TipoDeRegra Tipo { get; private set; }

    public ConfiguracaoDeRegra Configuracao { get; private set; }

    /// <summary>Quanto esta regra soma ao score quando dispara.</summary>
    public int Pontos { get; private set; }

    public DateTimeOffset PublicadaEm { get; private set; }

    public static VersaoDeRegra Publicar(
        Regra regra,
        int numero,
        ConfiguracaoDeRegra configuracao,
        int pontos,
        DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(regra);
        ArgumentNullException.ThrowIfNull(configuracao);

        if (numero < 1)
        {
            throw new ViolacaoDeInvariante("Numero da versao comeca em 1.");
        }

        if (configuracao.Tipo != regra.Tipo)
        {
            throw new ViolacaoDeInvariante(
                $"Configuracao de {configuracao.Tipo} nao serve para uma regra de {regra.Tipo}.");
        }

        if (pontos is < 0 or > PontosMaximos)
        {
            throw new ViolacaoDeInvariante($"Pontos devem estar entre 0 e {PontosMaximos}.");
        }

        // Validar aqui e o que impede configuracao invalida de existir no
        // banco - e, portanto, de chegar ao motor (ROADMAP secao 3.7).
        configuracao.Validar();

        return new VersaoDeRegra(
            Identificador.Novo(),
            regra.OrganizacaoId,
            regra.Id,
            numero,
            regra.Tipo,
            configuracao,
            pontos,
            agora);
    }
}
