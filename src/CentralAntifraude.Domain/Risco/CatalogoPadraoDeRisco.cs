namespace CentralAntifraude.Domain.Risco;

/// <summary>Perfil, regras e versoes recem-provisionados para uma organizacao.</summary>
public sealed record CatalogoProvisionado(
    PerfilDeRisco Perfil,
    VersaoDePerfilDeRisco VersaoDoPerfil,
    IReadOnlyList<Regra> Regras,
    IReadOnlyList<VersaoDeRegra> VersoesDeRegra);

/// <summary>
/// Catalogo inicial de regras e o perfil de risco padrao.
///
/// **Todos os numeros aqui sao configuracao de demonstracao.**
///
/// As PRATICAS sao reais e tem fonte: velocity checks, dispositivo
/// desconhecido, valor acima do gasto habitual e divergencia geografica estao
/// todas descritas no white paper "Card-Not-Present (CNP) Fraud Mitigation
/// Techniques" do U.S. Payments Forum (julho de 2020), consultado em
/// 2026-09-04 — ver docs/adr/0008-motor-de-risco.md para as citacoes.
///
/// Os NUMEROS — quantos pontos cada regra soma, onde ficam os limiares — nao
/// vem de fonte nenhuma. Sao escolhas deste projeto, feitas para que a
/// demonstracao produza casos interessantes, incluindo um falso positivo.
/// Apresenta-los como padrao de mercado seria inventar (CLAUDE.md secoes 22 e
/// 112).
///
/// A partir da Fase 8 o Supervisor gerencia isso pela interface; aqui o
/// catalogo e provisionado junto da organizacao para que nenhuma organizacao
/// exista sem perfil ativo.
/// </summary>
public static class CatalogoPadraoDeRisco
{
    public const string NomeDoPerfilPadrao = "Perfil padrao";

    // ---------------------------------------------------------------------
    // Limiares de demonstracao.
    //
    // Escolhidos para que uma regra sozinha nunca bloqueie: bloquear exige
    // ao menos duas evidencias independentes. Um unico sinal levar direto ao
    // bloqueio geraria muito falso positivo e tornaria a investigacao humana
    // - o proposito do produto - decorativa.
    // ---------------------------------------------------------------------

    /// <summary>A partir de 40 pontos, a recomendacao e revisar.</summary>
    public const int LimiarDeRevisao = 40;

    /// <summary>A partir de 70 pontos, a recomendacao e bloquear.</summary>
    public const int LimiarDeBloqueio = 70;

    /// <summary>
    /// As quatro regras do catalogo, com nome, configuracao e peso.
    ///
    /// Quatro, e nao uma duzia (ROADMAP secao 3.3): cada uma reconhece um
    /// padrao diferente e produz um caso demonstravel. Regras que so
    /// repetiriam o que outra ja diz nao entram.
    /// </summary>
    public static IReadOnlyList<(TipoDeRegra Tipo, string Nome, ConfiguracaoDeRegra Configuracao, int Pontos)>
        Definicoes =>
    [
        // Card testing: varias tentativas em sequencia para descobrir quais
        // credenciais ainda funcionam. Peso alto porque o padrao e especifico.
        (
            TipoDeRegra.VelocidadePorCliente,
            "Velocidade por cliente",
            new ConfiguracaoDeVelocidade(MaximoDeTransacoes: 3, JanelaEmMinutos: 10),
            35
        ),

        // Sozinho, dispositivo novo e fraco: gente troca de celular. Peso
        // baixo, para somar com outra evidencia em vez de decidir sozinho.
        (
            TipoDeRegra.NovoDispositivo,
            "Dispositivo novo",
            new ConfiguracaoDeNovoDispositivo(MinimoDeTransacoesNoHistorico: 3),
            20
        ),

        // Valor muito acima do habitual: forte quando ha historico suficiente
        // para que "habitual" signifique alguma coisa.
        (
            TipoDeRegra.ValorAcimaDoHistorico,
            "Valor acima do historico",
            new ConfiguracaoDeValorAcimaDoHistorico(
                MultiploDaMedia: 5m,
                MinimoDeTransacoesNoHistorico: 3),
            30
        ),

        // Pais diferente do habitual. Peso moderado: viagem e turismo geram
        // o mesmo sinal, e e justamente o caso que vira falso positivo
        // legitimo na demonstracao da Fase 13.
        (
            TipoDeRegra.DivergenciaGeografica,
            "Divergencia geografica",
            new ConfiguracaoDeDivergenciaGeografica(MinimoDeTransacoesNoHistorico: 3),
            25
        ),
    ];

    /// <summary>
    /// Cria perfil, regras e as primeiras versoes de tudo para uma organizacao.
    ///
    /// Chamado quando a organizacao nasce. Uma organizacao sem perfil ativo
    /// nao teria como avaliar transacao nenhuma, e descobrir isso na primeira
    /// ingestao seria tarde.
    /// </summary>
    public static CatalogoProvisionado Provisionar(Guid organizacaoId, DateTimeOffset agora)
    {
        var perfil = PerfilDeRisco.Criar(organizacaoId, NomeDoPerfilPadrao, agora);

        var regras = new List<Regra>();
        var versoes = new List<VersaoDeRegra>();

        foreach (var (tipo, nome, configuracao, pontos) in Definicoes)
        {
            // Mesmo caminho que o Supervisor usa na Fase 8: a regra nasce com
            // rascunho e a publicacao o congela. Um segundo caminho ate uma
            // versao publicada seria um segundo lugar onde as invariantes
            // teriam que ser lembradas.
            var regra = Regra.Criar(organizacaoId, tipo, nome, configuracao, pontos, agora);

            regras.Add(regra);
            versoes.Add(regra.PublicarRascunho(ultimaPublicada: null, agora));
        }

        var versaoDoPerfil = VersaoDePerfilDeRisco.Publicar(
            perfil,
            numero: 1,
            LimiarDeRevisao,
            LimiarDeBloqueio,
            versoes,
            agora);

        return new CatalogoProvisionado(perfil, versaoDoPerfil, regras, versoes);
    }
}
