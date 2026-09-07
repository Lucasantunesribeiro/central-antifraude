using CentralAntifraude.Application.Comum;
using CentralAntifraude.Domain.Backtests;

namespace CentralAntifraude.Api.Backtests;

// ---------------------------------------------------------------------------
// Entrada.
//
// **O que estes DTOs nao tem e a defesa.** Nao existe campo `status`,
// `resultado`, `candidato` nem `organizacaoId`: o perfil candidato e montado
// pelo servidor a partir do rascunho que ja esta no banco, e nao aceito do
// cliente. Aceitar a configuracao pelo corpo transformaria o backtest numa
// porta lateral para executar regra que nunca passou pelo catalogo fechado.
//
// O JSON estrito da aplicacao recusa campo desconhecido, entao mandar
// `"candidato": {...}` aqui nao e ignorado em silencio: e `400`.
// ---------------------------------------------------------------------------

/// <summary>
/// Pede uma simulacao.
///
/// <c>RegraId</c> nulo significa "so os limiares mudam". Limiares nulos
/// herdam os do perfil vigente.
/// </summary>
public sealed record SolicitarBacktestRequisicao(
    Guid? RegraId,
    int? LimiarDeRevisao,
    int? LimiarDeBloqueio,
    DateTimeOffset Inicio,
    DateTimeOffset Fim);

/// <summary>Interrompe uma execucao pendente ou em andamento.</summary>
public sealed record CancelarBacktestRequisicao(int Versao);

// ---------------------------------------------------------------------------
// Saida.
// ---------------------------------------------------------------------------

/// <summary>Uma regra do perfil candidato, como a tela a mostra.</summary>
public sealed record RegraCandidataResposta(
    Guid RegraId,
    string Nome,
    string Tipo,
    string Configuracao,
    int Pontos,
    string Origem)
{
    public static RegraCandidataResposta De(RegraCandidata regra)
    {
        ArgumentNullException.ThrowIfNull(regra);

        return new RegraCandidataResposta(
            regra.RegraId,
            regra.Nome,
            regra.Tipo.ToString(),
            // A frase deterministica do proprio dominio, e nao os numeros
            // crus: e a mesma que explica um sinal na tela da transacao.
            regra.Configuracao.Descrever(),
            regra.Pontos,
            regra.Origem.ToString());
    }
}

/// <summary>O perfil que seria publicado.</summary>
public sealed record PerfilCandidatoResposta(
    int LimiarDeRevisao,
    int LimiarDeBloqueio,
    IReadOnlyList<RegraCandidataResposta> Regras)
{
    public static PerfilCandidatoResposta De(PerfilCandidato candidato)
    {
        ArgumentNullException.ThrowIfNull(candidato);

        return new PerfilCandidatoResposta(
            candidato.LimiarDeRevisao,
            candidato.LimiarDeBloqueio,
            [.. candidato.Regras.Select(RegraCandidataResposta.De)]);
    }
}

/// <summary>Uma execucao na listagem: sem o candidato, que so o detalhe usa.</summary>
public sealed record BacktestResumido(
    Guid Id,
    string Descricao,
    Guid? RegraCandidataId,
    string Status,
    DateTimeOffset Inicio,
    DateTimeOffset Fim,
    int NumeroDaVersaoDePerfilVigente,
    string SolicitadaPor,
    DateTimeOffset SolicitadaEm,
    DateTimeOffset? ConcluidaEm,
    int? TotalAnalisado,
    int? TotalDeMudancas,
    string? MensagemDeErro,
    int Versao)
{
    public static BacktestResumido De(ExecucaoDeBacktest execucao)
    {
        ArgumentNullException.ThrowIfNull(execucao);

        return new BacktestResumido(
            execucao.Id,
            execucao.Descricao,
            execucao.RegraCandidataId,
            execucao.Status.ToString(),
            execucao.Inicio,
            execucao.Fim,
            execucao.NumeroDaVersaoDePerfilVigente,
            execucao.SolicitadaPorDescricao,
            execucao.SolicitadaEm,
            execucao.ConcluidaEm,
            execucao.Resultado?.TotalAnalisado,
            execucao.Resultado?.TotalDeMudancas,
            execucao.MensagemDeErro,
            execucao.Versao);
    }
}

/// <summary>Quantas transacoes cairiam em cada decisao.</summary>
public sealed record DistribuicaoResposta(int Permitir, int Revisar, int Bloquear)
{
    public static DistribuicaoResposta De(DistribuicaoDeDecisoes distribuicao)
    {
        ArgumentNullException.ThrowIfNull(distribuicao);

        return new DistribuicaoResposta(
            distribuicao.Permitir,
            distribuicao.Revisar,
            distribuicao.Bloquear);
    }
}

/// <summary>Uma transicao de decisao que o candidato provocaria.</summary>
public sealed record MudancaResposta(string De, string Para, int Quantidade);

/// <summary>
/// O cruzamento entre o veredito humano e o que cada perfil decidiria.
///
/// <c>Veredito</c> nulo e "sem resultado conhecido" — a maioria das
/// transacoes, porque so uma fracao delas vira caso investigado.
/// </summary>
public sealed record LinhaPorVereditoResposta(
    string? Veredito,
    int Total,
    DistribuicaoResposta Vigente,
    DistribuicaoResposta Candidato);

/// <summary>Uma faixa de score e quantas transacoes caem nela em cada perfil.</summary>
public sealed record FaixaDeScoreResposta(int De, int Ate, int Vigente, int Candidato);

/// <summary>O documento de apuracao.</summary>
public sealed record ResultadoResposta(
    int TotalAnalisado,
    int TotalQueAcionaria,
    int TotalDeMudancas,
    DistribuicaoResposta Vigente,
    DistribuicaoResposta Candidato,
    IReadOnlyList<MudancaResposta> Mudancas,
    IReadOnlyList<LinhaPorVereditoResposta> PorVeredito,
    IReadOnlyList<FaixaDeScoreResposta> FaixasDeScore)
{
    public static ResultadoResposta De(ResultadoDoBacktest resultado)
    {
        ArgumentNullException.ThrowIfNull(resultado);

        return new ResultadoResposta(
            resultado.TotalAnalisado,
            resultado.TotalQueAcionaria,
            resultado.TotalDeMudancas,
            DistribuicaoResposta.De(resultado.Vigente),
            DistribuicaoResposta.De(resultado.Candidato),
            [
                .. resultado.Mudancas.Select(m => new MudancaResposta(
                    m.De.ToString(),
                    m.Para.ToString(),
                    m.Quantidade)),
            ],
            [
                .. resultado.PorVeredito.Select(l => new LinhaPorVereditoResposta(
                    l.Veredito?.ToString(),
                    l.Total,
                    DistribuicaoResposta.De(l.Vigente),
                    DistribuicaoResposta.De(l.Candidato))),
            ],
            [
                .. resultado.FaixasDeScore.Select(f => new FaixaDeScoreResposta(
                    f.De,
                    f.Ate,
                    f.Vigente,
                    f.Candidato)),
            ]);
    }
}

/// <summary>Uma execucao com tudo: o candidato congelado e o resultado.</summary>
public sealed record BacktestDetalhado(
    BacktestResumido Execucao,
    PerfilCandidatoResposta Candidato,
    ResultadoResposta? Resultado)
{
    public static BacktestDetalhado De(ExecucaoDeBacktest execucao)
    {
        ArgumentNullException.ThrowIfNull(execucao);

        return new BacktestDetalhado(
            BacktestResumido.De(execucao),
            PerfilCandidatoResposta.De(execucao.Candidato),
            execucao.Resultado is null ? null : ResultadoResposta.De(execucao.Resultado));
    }
}

/// <summary>Uma pagina de execucoes.</summary>
public sealed record PaginaDeBacktests(
    IReadOnlyList<BacktestResumido> Itens,
    int Pagina,
    int Tamanho,
    long TotalDeItens,
    int TotalDePaginas)
{
    public static PaginaDeBacktests De(Pagina<ExecucaoDeBacktest> pagina)
    {
        ArgumentNullException.ThrowIfNull(pagina);

        return new PaginaDeBacktests(
            [.. pagina.Itens.Select(BacktestResumido.De)],
            pagina.PaginaAtual,
            pagina.TamanhoDaPagina,
            pagina.TotalDeItens,
            pagina.TotalDePaginas);
    }
}
