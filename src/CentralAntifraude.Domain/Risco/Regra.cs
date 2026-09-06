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
/// **A regra e mutavel; a versao publicada nao.** E aqui que mora o rascunho
/// — a configuracao que o Supervisor esta preparando e que ainda nao vale
/// para ninguem. Publicar transforma o rascunho em uma versao imutavel e
/// esvazia o rascunho (CLAUDE.md secao 23).
///
/// <code>
/// Rascunho  →  Publicada (v1)  →  novo rascunho  →  Publicada (v2)
/// </code>
///
/// <see cref="Versao"/> nao tem nada a ver com o numero da versao publicada:
/// e o token de concorrencia administrativa (ROADMAP 8.6). Dois supervisores
/// editando a mesma regra ao mesmo tempo nao podem se sobrescrever em
/// silencio.
/// </summary>
public sealed class Regra
{
    public const int TamanhoMinimoDoNome = 3;
    public const int TamanhoMaximoDoNome = 120;

    private Regra(
        Guid id,
        Guid organizacaoId,
        TipoDeRegra tipo,
        string nome,
        DateTimeOffset agora)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        Tipo = tipo;
        Nome = nome;
        Ativa = true;
        CriadaEm = agora;
        AtualizadaEm = agora;
        Versao = 1;
    }

    // Construtor usado pelo EF Core na materializacao.
    private Regra() => Nome = string.Empty;

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public TipoDeRegra Tipo { get; private set; }

    /// <summary>
    /// Rotulo da identidade, e nao parte da versao.
    ///
    /// Renomear nao reescreve o passado: a explicacao de cada sinal ja foi
    /// congelada no proprio sinal no momento da avaliacao
    /// (<see cref="SinalDeRisco.Explicacao"/>). O nome serve para a equipe
    /// reconhecer a regra hoje.
    /// </summary>
    public string Nome { get; private set; }

    /// <summary>
    /// Regra desativada nao entra nas proximas versoes do perfil.
    ///
    /// Nao e exclusao: as versoes ja publicadas continuam existindo, e as
    /// avaliacoes que as usaram continuam explicaveis. Reativar traz a regra
    /// de volta com a ultima versao que ela tinha.
    /// </summary>
    public bool Ativa { get; private set; }

    /// <summary>Numero da ultima versao publicada. Zero enquanto so houver rascunho.</summary>
    public int NumeroDaUltimaVersao { get; private set; }

    /// <summary>Configuracao em preparo. Nula quando nao ha rascunho aberto.</summary>
    public ConfiguracaoDeRegra? ConfiguracaoEmRascunho { get; private set; }

    /// <summary>Peso em preparo. Nulo quando nao ha rascunho aberto.</summary>
    public int? PontosEmRascunho { get; private set; }

    public DateTimeOffset CriadaEm { get; private set; }

    public DateTimeOffset AtualizadaEm { get; private set; }

    /// <summary>Token de concorrencia administrativa. Sobe a cada alteracao.</summary>
    public int Versao { get; private set; }

    public bool TemRascunho => ConfiguracaoEmRascunho is not null && PontosEmRascunho is not null;

    public bool FoiPublicada => NumeroDaUltimaVersao > 0;

    /// <summary>
    /// Cria a regra ja com o primeiro rascunho.
    ///
    /// Uma regra sem configuracao nenhuma nao significa nada — nao daria para
    /// publicar, nem para explicar o que ela pretende reconhecer. Por isso
    /// nascer e escrever o rascunho sao a mesma operacao.
    /// </summary>
    public static Regra Criar(
        Guid organizacaoId,
        TipoDeRegra tipo,
        string nome,
        ConfiguracaoDeRegra configuracao,
        int pontos,
        DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        if (organizacaoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Regra precisa pertencer a uma organizacao.");
        }

        if (!Enum.IsDefined(tipo))
        {
            throw new ViolacaoDeInvariante("Tipo de regra fora do catalogo.");
        }

        var regra = new Regra(Identificador.Novo(), organizacaoId, tipo, ValidarNome(nome), agora);

        regra.EscreverRascunho(configuracao, pontos);

        return regra;
    }

    /// <summary>
    /// Grava o rascunho e aplica o nome.
    ///
    /// O nome vale imediatamente; a configuracao e o peso so passam a valer na
    /// publicacao. Sao coisas de natureza diferente: um e rotulo, os outros
    /// mudam o comportamento do motor.
    /// </summary>
    public void SalvarRascunho(
        string nome,
        ConfiguracaoDeRegra configuracao,
        int pontos,
        DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        Nome = ValidarNome(nome);

        EscreverRascunho(configuracao, pontos);

        Tocar(agora);
    }

    /// <summary>
    /// Joga fora o rascunho e volta a valer a ultima versao publicada.
    ///
    /// Uma regra que nunca foi publicada nao pode descartar: ela ficaria sem
    /// configuracao nenhuma, viva no catalogo e impossivel de publicar.
    /// </summary>
    public void DescartarRascunho(DateTimeOffset agora)
    {
        if (!TemRascunho)
        {
            throw new ViolacaoDeInvariante("Nao ha rascunho para descartar.");
        }

        if (!FoiPublicada)
        {
            throw new ViolacaoDeInvariante(
                "Esta regra nunca foi publicada. Descartar o rascunho a deixaria sem " +
                "configuracao nenhuma — desative a regra em vez de descartar.");
        }

        LimparRascunho();

        Tocar(agora);
    }

    /// <summary>
    /// Congela o rascunho em uma versao nova.
    ///
    /// <paramref name="ultimaPublicada"/> entra para que uma publicacao que
    /// nao muda nada seja recusada: uma v2 identica a v1 faria o historico de
    /// versoes afirmar uma mudanca que nao houve, e e exatamente esse
    /// historico que a Fase 9 usa para comparar regras.
    /// </summary>
    public VersaoDeRegra PublicarRascunho(VersaoDeRegra? ultimaPublicada, DateTimeOffset agora)
    {
        if (!TemRascunho)
        {
            throw new ViolacaoDeInvariante(
                "Nao ha rascunho para publicar. Edite a regra antes de publicar.");
        }

        if (!Ativa)
        {
            throw new ViolacaoDeInvariante(
                "Regra desativada nao publica versao. Reative antes de publicar.");
        }

        if (ultimaPublicada is not null &&
            ultimaPublicada.Configuracao == ConfiguracaoEmRascunho &&
            ultimaPublicada.Pontos == PontosEmRascunho)
        {
            throw new ViolacaoDeInvariante(
                "O rascunho e igual a versao publicada. Nao ha o que publicar.");
        }

        var versao = VersaoDeRegra.Publicar(
            this,
            NumeroDaUltimaVersao + 1,
            ConfiguracaoEmRascunho!,
            PontosEmRascunho!.Value,
            agora);

        NumeroDaUltimaVersao = versao.Numero;

        LimparRascunho();

        Tocar(agora);

        return versao;
    }

    public void Desativar(DateTimeOffset agora)
    {
        if (!Ativa)
        {
            throw new ViolacaoDeInvariante("Esta regra ja esta desativada.");
        }

        Ativa = false;

        Tocar(agora);
    }

    public void Reativar(DateTimeOffset agora)
    {
        if (Ativa)
        {
            throw new ViolacaoDeInvariante("Esta regra ja esta ativa.");
        }

        Ativa = true;

        Tocar(agora);
    }

    private void EscreverRascunho(ConfiguracaoDeRegra configuracao, int pontos)
    {
        if (configuracao.Tipo != Tipo)
        {
            throw new ViolacaoDeInvariante(
                $"Configuracao de {configuracao.Tipo} nao serve para uma regra de {Tipo}.");
        }

        if (pontos is < 1 or > VersaoDeRegra.PontosMaximos)
        {
            throw new ViolacaoDeInvariante(
                $"Pontos devem estar entre 1 e {VersaoDeRegra.PontosMaximos}.");
        }

        // Validar no rascunho, e nao so na publicacao: o Supervisor precisa
        // saber que a configuracao esta errada enquanto ainda esta editando.
        configuracao.Validar();

        ConfiguracaoEmRascunho = configuracao;
        PontosEmRascunho = pontos;
    }

    private void LimparRascunho()
    {
        ConfiguracaoEmRascunho = null;
        PontosEmRascunho = null;
    }

    private void Tocar(DateTimeOffset agora)
    {
        AtualizadaEm = agora;
        Versao++;
    }

    private static string ValidarNome(string nome)
    {
        var limpo = nome?.Trim() ?? string.Empty;

        if (limpo.Length < TamanhoMinimoDoNome || limpo.Length > TamanhoMaximoDoNome)
        {
            throw new ViolacaoDeInvariante(
                $"Nome da regra deve ter entre {TamanhoMinimoDoNome} e " +
                $"{TamanhoMaximoDoNome} caracteres.");
        }

        return limpo;
    }
}

/// <summary>
/// Uma versao publicada de uma regra: a configuracao e o peso congelados.
///
/// **Imutavel apos a publicacao** (CLAUDE.md secao 23). Nao ha metodo que
/// altere configuracao ou pontos — nao por convencao, mas porque nao existe.
/// Um teste de arquitetura verifica que continua assim.
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
