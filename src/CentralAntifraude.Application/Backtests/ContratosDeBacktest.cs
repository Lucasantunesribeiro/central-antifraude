using CentralAntifraude.Application.Comum;
using CentralAntifraude.Domain.Backtests;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Application.Backtests;

/// <summary>
/// Os limites que impedem um backtest de virar custo ou negacao de servico
/// (ROADMAP 9.7).
///
/// Todos configuraveis, e todos validados na inicializacao: um limite
/// absurdo em configuracao derruba o processo em vez de aparecer como uma
/// consulta que nunca termina.
/// </summary>
public sealed class OpcoesDeBacktest
{
    public const string Secao = "Backtest";

    /// <summary>
    /// Maior janela historica que uma execucao pode pedir.
    ///
    /// Casada com o padrao de <c>Avaliacao:DiasDeHistorico</c>: uma janela
    /// maior do que o passado que o motor enxerga produziria transacoes
    /// avaliadas com contexto mais pobre do que tiveram de verdade.
    /// </summary>
    public int JanelaMaximaEmDias { get; set; } = 90;

    /// <summary>
    /// Teto de transacoes analisadas em uma execucao.
    ///
    /// Estourar o teto **recusa** a execucao em vez de truncar. Truncar
    /// devolveria um resultado que parece completo e descreve um pedaco
    /// arbitrario do periodo — e uma decisao de publicar regra tomada sobre
    /// isso seria pior do que nao ter backtest nenhum.
    /// </summary>
    public int MaximoDeTransacoesAnalisadas { get; set; } = 5_000;

    /// <summary>
    /// Teto de transacoes carregadas como contexto historico.
    ///
    /// O contexto cobre a janela pedida MAIS o passado que cada transacao
    /// enxergava, entao ele e sempre maior do que o conjunto analisado. Sem
    /// teto proprio, uma organizacao movimentada faria o worker carregar a
    /// tabela inteira na memoria.
    /// </summary>
    public int MaximoDeTransacoesDeContexto { get; set; } = 50_000;

    /// <summary>
    /// Quantas execucoes uma organizacao pode ter em andamento ao mesmo tempo.
    ///
    /// E a defesa contra spam de execucoes: sem ela, um clique repetido
    /// enfileira trabalho ilimitado, e a fila de backtests — que e separada
    /// justamente para nao atrapalhar a operacao — passaria a atrapalhar a si
    /// mesma.
    /// </summary>
    public int MaximoDeExecucoesEmAndamento { get; set; } = 2;

    /// <summary>
    /// Depois disto, uma execucao presa em andamento e considerada morta e
    /// pode ser retomada por outro worker.
    /// </summary>
    public int TempoMaximoDeExecucaoEmSegundos { get; set; } = 300;

    public TimeSpan JanelaMaxima => TimeSpan.FromDays(JanelaMaximaEmDias);

    public TimeSpan TempoMaximoDeExecucao => TimeSpan.FromSeconds(TempoMaximoDeExecucaoEmSegundos);

    public void Validar()
    {
        if (JanelaMaximaEmDias is < 1 or > 3_650)
        {
            throw new InvalidOperationException($"{Secao}:JanelaMaximaEmDias deve estar entre 1 e 3650.");
        }

        if (MaximoDeTransacoesAnalisadas is < 1 or > 200_000)
        {
            throw new InvalidOperationException(
                $"{Secao}:MaximoDeTransacoesAnalisadas deve estar entre 1 e 200000.");
        }

        if (MaximoDeTransacoesDeContexto < MaximoDeTransacoesAnalisadas)
        {
            // O contexto sempre inclui o conjunto analisado. Um teto menor
            // faria toda execucao no limite falhar por um motivo que a
            // mensagem nao explicaria.
            throw new InvalidOperationException(
                $"{Secao}:MaximoDeTransacoesDeContexto nao pode ser menor que " +
                $"{Secao}:MaximoDeTransacoesAnalisadas.");
        }

        if (MaximoDeExecucoesEmAndamento is < 1 or > 50)
        {
            throw new InvalidOperationException(
                $"{Secao}:MaximoDeExecucoesEmAndamento deve estar entre 1 e 50.");
        }

        if (TempoMaximoDeExecucaoEmSegundos is < 10 or > 3_600)
        {
            throw new InvalidOperationException(
                $"{Secao}:TempoMaximoDeExecucaoEmSegundos deve estar entre 10 e 3600.");
        }
    }
}

/// <summary>
/// Uma transacao do passado, com o identificador e o cliente ao lado da
/// projecao que as regras leem.
///
/// O identificador esta aqui por um motivo so: excluir a propria transacao do
/// contexto dela. Sem isso, a regra de valor compararia a transacao com ela
/// mesma — que e exatamente o que o provedor de producao evita com
/// <c>t.Id != transacao.Id</c>.
/// </summary>
public sealed record LinhaDoHistorico(
    Guid Id,
    string ClienteExternoId,
    TransacaoDoHistorico Dados);

/// <summary>
/// Tudo o que uma execucao precisa, carregado de uma vez.
///
/// **Uma consulta, e nao uma por transacao.** O provedor de producao carrega o
/// contexto de UMA transacao no caminho critico; aqui sao milhares, e repetir
/// aquela consulta por transacao transformaria um backtest em cinco mil idas
/// ao banco. O passado e lido uma vez e recortado em memoria — com a mesma
/// janela e o mesmo teto de <see cref="OpcoesDeAvaliacao"/>, senao "mesmo
/// motor" seria promessa vazia.
/// </summary>
public sealed record DadosDoBacktest(
    IReadOnlyList<Transacao> Analisadas,
    IReadOnlyList<LinhaDoHistorico> Contexto,
    IReadOnlyDictionary<Guid, ResultadoDaInvestigacao> Vereditos);

/// <summary>
/// O passado a carregar como contexto passou do teto.
///
/// Recusar, e nao truncar: um contexto cortado faria as transacoes mais
/// antigas da janela serem avaliadas com menos historico do que tiveram de
/// verdade, e o backtest passaria a medir a propria limitacao.
/// </summary>
public sealed class BacktestExcedeuOContexto : Exception
{
    public BacktestExcedeuOContexto(int limite)
        : base(
            $"O periodo pedido exige carregar mais de {limite} transacoes como contexto historico. " +
            "Escolha um periodo menor.")
    {
    }
}

/// <summary>Acesso as execucoes de backtest e aos dados historicos que elas leem.</summary>
public interface IRepositorioDeBacktests
{
    Task<Pagina<ExecucaoDeBacktest>> ListarAsync(
        ParametrosDePaginacao parametros,
        CancellationToken cancellationToken);

    /// <summary>Uma execucao do tenant atual. Nula quando nao existe daqui.</summary>
    Task<ExecucaoDeBacktest?> BuscarAsync(Guid execucaoId, CancellationToken cancellationToken);

    /// <summary>Quantas execucoes do tenant estao pendentes ou em andamento.</summary>
    Task<int> ContarEmAndamentoAsync(CancellationToken cancellationToken);

    /// <summary>Quantas transacoes do tenant caem na janela, por <c>OcorridaEm</c>.</summary>
    Task<int> ContarTransacoesNaJanelaAsync(
        DateTimeOffset inicio,
        DateTimeOffset fim,
        CancellationToken cancellationToken);

    void Adicionar(ExecucaoDeBacktest execucao);

    // -----------------------------------------------------------------------
    // Execucao (worker)
    //
    // O worker roda fora de uma requisicao e nao tem identidade, entao o
    // filtro global de tenant devolveria vazio. Estes metodos o desligam pelo
    // nome e recebem a organizacao explicitamente — a mesma disciplina do
    // despachante da Outbox.
    // -----------------------------------------------------------------------

    /// <summary>
    /// A execucao, sem passar pelo filtro de tenant.
    ///
    /// Quem chama confere a organizacao contra o que o envelope declara. O
    /// envelope e dado, nunca autoridade.
    /// </summary>
    Task<ExecucaoDeBacktest?> BuscarParaExecucaoAsync(
        Guid execucaoId,
        CancellationToken cancellationToken);

    /// <summary>A versao de perfil que a execucao congelou como comparacao.</summary>
    Task<VersaoDePerfilDeRisco?> BuscarVersaoDePerfilAsync(
        Guid organizacaoId,
        Guid versaoDePerfilId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Transacoes da janela, contexto historico e vereditos humanos.
    ///
    /// Tudo somente leitura: nada do que sai daqui e rastreado pelo contexto
    /// de persistencia, porque nada disto pode ser alterado por um backtest.
    /// </summary>
    Task<DadosDoBacktest> CarregarDadosAsync(
        ExecucaoDeBacktest execucao,
        TimeSpan janelaDeHistorico,
        int maximoDeContexto,
        CancellationToken cancellationToken);
}
