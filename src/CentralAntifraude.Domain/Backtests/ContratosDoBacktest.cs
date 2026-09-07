using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.Domain.Backtests;

/// <summary>
/// Onde a execucao esta.
///
/// Vocabulario fechado, como o resto do produto. Os tres estados finais sao
/// distintos de proposito: <see cref="Falhou"/> e defeito ou limite estourado,
/// <see cref="Cancelada"/> e decisao humana, e confundir os dois faria a tela
/// dizer que o sistema quebrou quando alguem apenas desistiu.
/// </summary>
public enum StatusDoBacktest
{
    /// <summary>Solicitada e enfileirada. Nenhum worker pegou ainda.</summary>
    Pendente = 1,

    /// <summary>Um worker esta executando.</summary>
    Executando = 2,

    /// <summary>Terminou e tem resultado.</summary>
    Concluida = 3,

    /// <summary>Terminou sem resultado, com motivo registrado.</summary>
    Falhou = 4,

    /// <summary>Interrompida por decisao de quem a solicitou.</summary>
    Cancelada = 5,
}

/// <summary>De onde saiu a configuracao de uma regra do perfil candidato.</summary>
public enum OrigemDaRegraCandidata
{
    /// <summary>A ultima versao publicada. E o que o motor ja executa hoje.</summary>
    Publicada = 1,

    /// <summary>O rascunho em preparo. E a mudanca que o backtest esta medindo.</summary>
    Rascunho = 2,
}

/// <summary>
/// Uma regra dentro do perfil candidato, congelada no momento da solicitacao.
///
/// **O snapshot e o ponto inteiro** (ROADMAP 9.2). O rascunho de uma regra
/// muda a qualquer momento; se o worker fosse le-lo na hora de executar, o
/// resultado descreveria uma configuracao diferente da que foi pedida — e
/// ninguem saberia disso olhando a tela.
/// </summary>
public sealed record RegraCandidata(
    Guid RegraId,
    string Nome,
    TipoDeRegra Tipo,
    ConfiguracaoDeRegra Configuracao,
    int Pontos,
    OrigemDaRegraCandidata Origem);

/// <summary>
/// O perfil que **seria publicado**, congelado.
///
/// A Fase 8 estabeleceu que uma unica coisa muda o que o motor executa:
/// publicar uma versao de perfil. Por isso o candidato de um backtest e
/// exatamente isso — a composicao que resultaria da publicacao — e nao uma
/// regra solta. Simular uma regra fora do perfil responderia a uma pergunta
/// que o produto nao permite fazer.
/// </summary>
public sealed record PerfilCandidato(
    int LimiarDeRevisao,
    int LimiarDeBloqueio,
    IReadOnlyList<RegraCandidata> Regras)
{
    /// <summary>
    /// As mesmas invariantes de uma versao de perfil de verdade.
    ///
    /// Um candidato que nao poderia ser publicado nao deve ser simulado: o
    /// resultado descreveria um estado inalcancavel e induziria a decisao
    /// errada.
    /// </summary>
    public void Validar()
    {
        if (Regras is null || Regras.Count == 0)
        {
            throw new ViolacaoDeInvariante(
                "Um perfil candidato precisa de ao menos uma regra. Sem regra, " +
                "toda transacao receberia score zero.");
        }

        if (Regras.Select(r => r.RegraId).Distinct().Count() != Regras.Count)
        {
            throw new ViolacaoDeInvariante(
                "O perfil candidato nao pode conter duas versoes da mesma regra.");
        }

        if (LimiarDeRevisao is < VersaoDePerfilDeRisco.ScoreMinimo or > VersaoDePerfilDeRisco.ScoreMaximo ||
            LimiarDeBloqueio is < VersaoDePerfilDeRisco.ScoreMinimo or > VersaoDePerfilDeRisco.ScoreMaximo)
        {
            throw new ViolacaoDeInvariante(
                $"Limiares devem estar entre {VersaoDePerfilDeRisco.ScoreMinimo} e " +
                $"{VersaoDePerfilDeRisco.ScoreMaximo}.");
        }

        if (LimiarDeRevisao >= LimiarDeBloqueio)
        {
            throw new ViolacaoDeInvariante(
                "LimiarDeRevisao deve ser menor que LimiarDeBloqueio, " +
                "senao a faixa de revisao fica vazia.");
        }

        if (LimiarDeRevisao == VersaoDePerfilDeRisco.ScoreMinimo)
        {
            throw new ViolacaoDeInvariante(
                "LimiarDeRevisao igual a zero tornaria a decisao Permitir inalcancavel.");
        }

        foreach (var regra in Regras)
        {
            if (regra.Configuracao.Tipo != regra.Tipo)
            {
                throw new ViolacaoDeInvariante(
                    $"Configuracao de {regra.Configuracao.Tipo} nao serve para uma regra de {regra.Tipo}.");
            }

            if (regra.Pontos is < 0 or > VersaoDeRegra.PontosMaximos)
            {
                throw new ViolacaoDeInvariante(
                    $"Pontos devem estar entre 0 e {VersaoDeRegra.PontosMaximos}.");
            }

            regra.Configuracao.Validar();
        }
    }

    /// <summary>Regras que vieram do rascunho — a mudanca que se quer medir.</summary>
    public IReadOnlyList<RegraCandidata> Alteradas =>
        [.. Regras.Where(r => r.Origem == OrigemDaRegraCandidata.Rascunho)];
}

/// <summary>Quantas transacoes cairiam em cada decisao.</summary>
public sealed record DistribuicaoDeDecisoes(int Permitir, int Revisar, int Bloquear)
{
    public static DistribuicaoDeDecisoes Vazia { get; } = new(0, 0, 0);

    public int Total => Permitir + Revisar + Bloquear;

    public DistribuicaoDeDecisoes Somar(Decisao decisao) => decisao switch
    {
        Decisao.Permitir => this with { Permitir = Permitir + 1 },
        Decisao.Revisar => this with { Revisar = Revisar + 1 },
        Decisao.Bloquear => this with { Bloquear = Bloquear + 1 },
        _ => throw new ViolacaoDeInvariante($"Decisao desconhecida: {decisao}."),
    };
}

/// <summary>
/// Uma transicao de decisao entre o perfil vigente e o candidato.
///
/// So aparecem as que de fato mudam. "Revisar continua Revisar" nao e
/// informacao — o que o Supervisor precisa ver e o que passaria a ser
/// diferente se ele publicasse.
/// </summary>
public sealed record MudancaDeDecisao(Decisao De, Decisao Para, int Quantidade);

/// <summary>
/// Uma linha do cruzamento entre o que a investigacao humana concluiu e o que
/// cada perfil decidiria.
///
/// **Isto nao e precisao nem recall** (ROADMAP 9.6). Sao contagens brutas, com
/// o denominador visivel: de N transacoes que a equipe concluiu como fraude,
/// o candidato bloquearia X. Chamar isso de "taxa de deteccao" seria afirmar
/// algo que os dados nao sustentam — a maioria das transacoes nunca foi
/// investigada, e as que foram nao sao uma amostra aleatoria.
/// </summary>
public sealed record LinhaPorVeredito(
    ResultadoDaInvestigacao? Veredito,
    int Total,
    DistribuicaoDeDecisoes Vigente,
    DistribuicaoDeDecisoes Candidato);

/// <summary>Uma faixa de score e quantas transacoes caem nela em cada perfil.</summary>
public sealed record FaixaDeScore(int De, int Ate, int Vigente, int Candidato);

/// <summary>
/// O que o backtest apurou.
///
/// Guardado como snapshot no proprio registro da execucao: recalcular na hora
/// de exibir faria uma publicacao posterior mudar o resultado de um backtest
/// antigo — exatamente o que a Fase 8 impediu para as avaliacoes.
/// </summary>
public sealed record ResultadoDoBacktest(
    int TotalAnalisado,
    int TotalQueAcionaria,
    DistribuicaoDeDecisoes Vigente,
    DistribuicaoDeDecisoes Candidato,
    IReadOnlyList<MudancaDeDecisao> Mudancas,
    IReadOnlyList<LinhaPorVeredito> PorVeredito,
    IReadOnlyList<FaixaDeScore> FaixasDeScore)
{
    /// <summary>Quantas transacoes teriam decisao diferente sob o candidato.</summary>
    public int TotalDeMudancas => Mudancas.Sum(m => m.Quantidade);
}
