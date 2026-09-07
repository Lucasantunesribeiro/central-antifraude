using System.Diagnostics;
using CentralAntifraude.Application.Risco;
using CentralAntifraude.Domain.Backtests;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Tempo;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Application.Backtests;

/// <summary>O backtest nao pode terminar: passou do tempo maximo configurado.</summary>
public sealed class BacktestExcedeuOTempo : Exception
{
    public BacktestExcedeuOTempo(TimeSpan limite, int analisadas)
        : base(
            $"A execucao passou de {limite.TotalSeconds:0} segundos com {analisadas} transacao(oes) " +
            "analisadas e foi interrompida.")
    {
    }
}

/// <summary>O conjunto a analisar cresceu acima do limite entre o pedido e a execucao.</summary>
public sealed class BacktestExcedeuOVolume : Exception
{
    public BacktestExcedeuOVolume(int encontradas, int limite)
        : base(
            $"A janela passou a ter {encontradas} transacoes e o limite por execucao e {limite}. " +
            "Solicite de novo com um periodo menor.")
    {
    }
}

/// <summary>
/// Roda o backtest: o mesmo motor, duas vezes por transacao.
///
/// **Nao existe um segundo motor** (ROADMAP 9.4). O que muda em relacao a
/// producao sao duas coisas, e nenhuma delas e a semantica das regras:
///
/// 1. **de onde vem o perfil** — em producao, a versao publicada; aqui, o
///    candidato montado em memoria por <see cref="PerfilSimulado"/>;
/// 2. **de onde vem o contexto** — em producao, uma consulta por avaliacao;
///    aqui, o passado inteiro carregado uma vez e recortado por transacao,
///    com a MESMA janela e o MESMO teto de <see cref="OpcoesDeAvaliacao"/>.
///
/// Que o recorte e fiel nao e promessa: o teste da fase roda um backtest cujo
/// candidato e igual ao perfil vigente e exige que cada decisao bata com a
/// avaliacao que ficou gravada na ingestao.
///
/// **Nada e gravado por este codigo.** As avaliacoes que o motor produz aqui
/// vivem na memoria e sao descartadas; o unico resultado e o documento
/// agregado que o worker grava na propria linha da execucao.
/// </summary>
public sealed class ExecutorDeBacktest
{
    private readonly IRepositorioDeBacktests _backtests;
    private readonly MotorDeRisco _motor;
    private readonly OpcoesDeAvaliacao _avaliacao;
    private readonly OpcoesDeBacktest _opcoes;
    private readonly IRelogio _relogio;

    public ExecutorDeBacktest(
        IRepositorioDeBacktests backtests,
        MotorDeRisco motor,
        OpcoesDeAvaliacao avaliacao,
        OpcoesDeBacktest opcoes,
        IRelogio relogio)
    {
        _backtests = backtests;
        _motor = motor;
        _avaliacao = avaliacao;
        _opcoes = opcoes;
        _relogio = relogio;
    }

    public async Task<ResultadoDoBacktest> ApurarAsync(
        ExecucaoDeBacktest execucao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execucao);

        var vigente = await _backtests.BuscarVersaoDePerfilAsync(
            execucao.OrganizacaoId,
            execucao.VersaoDePerfilVigenteId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "A versao de perfil usada como comparacao nao existe mais. " +
                "Solicite o backtest de novo.");

        var agora = _relogio.Agora;
        var candidato = PerfilSimulado.Montar(execucao.OrganizacaoId, execucao.Candidato, agora);

        var dados = await _backtests.CarregarDadosAsync(
            execucao,
            _avaliacao.JanelaDeHistorico,
            _opcoes.MaximoDeTransacoesDeContexto,
            cancellationToken);

        if (dados.Analisadas.Count > _opcoes.MaximoDeTransacoesAnalisadas)
        {
            // O limite foi conferido no pedido, mas transacoes continuam
            // chegando enquanto a mensagem espera na fila. A checagem aqui e a
            // que vale.
            throw new BacktestExcedeuOVolume(
                dados.Analisadas.Count,
                _opcoes.MaximoDeTransacoesAnalisadas);
        }

        var historico = IndexarPorCliente(dados.Contexto);
        var apuracao = new ApuracaoDoBacktest();
        var cronometro = Stopwatch.StartNew();
        var analisadas = 0;

        // Ordem estavel, ainda que o resultado seja um agregado: um teste que
        // acuse diferenca precisa poder repetir a execucao e achar o mesmo
        // ponto.
        foreach (var transacao in dados.Analisadas.OrderBy(t => t.OcorridaEm).ThenBy(t => t.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (cronometro.Elapsed > _opcoes.TempoMaximoDeExecucao)
            {
                throw new BacktestExcedeuOTempo(_opcoes.TempoMaximoDeExecucao, analisadas);
            }

            var contexto = ContextoDe(transacao, historico);

            var comVigente = _motor.Avaliar(transacao, vigente, contexto, agora);
            var comCandidato = _motor.Avaliar(transacao, candidato, contexto, agora);

            apuracao.Registrar(new ObservacaoDoBacktest(
                comVigente.Score,
                comVigente.Decisao,
                comCandidato.Score,
                comCandidato.Decisao,
                comCandidato.Sinais.Count > 0,
                dados.Vereditos.TryGetValue(transacao.Id, out var veredito)
                    ? veredito
                    : (ResultadoDaInvestigacao?)null));

            analisadas++;
        }

        return apuracao.Concluir();
    }

    /// <summary>
    /// Agrupa o passado por cliente, cada grupo da transacao mais recente para
    /// a mais antiga.
    ///
    /// A ordem e a mesma do provedor de producao — <c>OcorridaEm</c> desc,
    /// depois <c>Id</c> desc — porque e ela que decide quais transacoes
    /// sobrevivem ao teto de historico. Ordenar diferente faria o contexto do
    /// backtest divergir do real justamente nos clientes mais movimentados.
    /// </summary>
    private static Dictionary<string, List<LinhaDoHistorico>> IndexarPorCliente(
        IReadOnlyList<LinhaDoHistorico> contexto) =>
        contexto
            .GroupBy(linha => linha.ClienteExternoId, StringComparer.Ordinal)
            .ToDictionary(
                grupo => grupo.Key,
                grupo => grupo
                    .OrderByDescending(linha => linha.Dados.OcorridaEm)
                    .ThenByDescending(linha => linha.Id)
                    .ToList(),
                StringComparer.Ordinal);

    /// <summary>
    /// O contexto que aquela transacao enxergava.
    ///
    /// Recorte identico ao do provedor de producao: mesma janela, mesmo teto,
    /// mesmo intervalo fechado ate <c>OcorridaEm</c> e a propria transacao
    /// fora — incluir-la faria a regra de valor comparar a transacao com ela
    /// mesma.
    /// </summary>
    private ContextoDeRisco ContextoDe(
        Transacao transacao,
        Dictionary<string, List<LinhaDoHistorico>> historico)
    {
        if (!historico.TryGetValue(transacao.ClienteExternoId, out var doCliente))
        {
            return ContextoDeRisco.Vazio;
        }

        var inicioDaJanela = transacao.OcorridaEm - _avaliacao.JanelaDeHistorico;

        return new ContextoDeRisco(
        [
            .. doCliente
                .Where(linha => linha.Id != transacao.Id)
                .Where(linha => linha.Dados.OcorridaEm >= inicioDaJanela
                    && linha.Dados.OcorridaEm <= transacao.OcorridaEm)
                .Take(_avaliacao.MaximoDeTransacoesNoHistorico)
                .Select(linha => linha.Dados),
        ]);
    }
}
