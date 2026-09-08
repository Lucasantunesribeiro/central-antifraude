using System.Diagnostics.Metrics;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Application.Observabilidade;
using CentralAntifraude.Infrastructure.Observabilidade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CentralAntifraude.Infrastructure.Mensageria;

/// <summary>Ritmo dos processos de fundo.</summary>
public sealed class OpcoesDeSegundoPlano
{
    public const string Secao = "SegundoPlano";

    /// <summary>
    /// Liga o despachante e o worker dentro do processo da API.
    ///
    /// **Desligado por padrao**, e isso e deliberado. Os testes de integracao
    /// precisam controlar quando cada ciclo roda: um laco de fundo ligado
    /// sozinho tornaria "quantas mensagens sobraram na fila" uma pergunta sem
    /// resposta estavel, e o resultado do teste passaria a depender de quem
    /// ganhou a corrida. Em desenvolvimento e producao ele e ligado por
    /// configuracao.
    ///
    /// Na Fase 14 estes lacos viram Lambdas; o codigo de dentro do ciclo nao
    /// muda.
    /// </summary>
    public bool Habilitado { get; set; }

    /// <summary>Intervalo entre ciclos quando nao havia nada a fazer.</summary>
    public int IntervaloOciosoEmMs { get; set; } = 2_000;

    /// <summary>
    /// Intervalo quando o ciclo anterior fez trabalho.
    ///
    /// Curto de proposito: se havia fila, provavelmente ainda ha. Esperar o
    /// intervalo cheio depois de um lote cheio faria a fila crescer sem
    /// necessidade.
    /// </summary>
    public int IntervaloAtivoEmMs { get; set; } = 100;

    /// <summary>
    /// Intervalo minimo entre duas amostras de profundidade de fila.
    ///
    /// Nao acompanha o ritmo do laco de proposito. O laco roda a cada 100 ms
    /// quando ha trabalho; medir profundidade nesse ritmo seriam dezenas de
    /// consultas por segundo para responder uma pergunta que muda em minutos.
    /// </summary>
    public int IntervaloDeAmostragemEmMs { get; set; } = 15_000;

    public void Validar()
    {
        if (IntervaloOciosoEmMs is < 50 or > 300_000)
        {
            throw new InvalidOperationException(
                $"{Secao}:IntervaloOciosoEmMs deve estar entre 50 e 300000.");
        }

        if (IntervaloAtivoEmMs is < 0 or > 60_000)
        {
            throw new InvalidOperationException(
                $"{Secao}:IntervaloAtivoEmMs deve estar entre 0 e 60000.");
        }

        if (IntervaloDeAmostragemEmMs is < 0 or > 3_600_000)
        {
            throw new InvalidOperationException(
                $"{Secao}:IntervaloDeAmostragemEmMs deve estar entre 0 e 3600000.");
        }
    }
}

/// <summary>
/// Base dos lacos de fundo.
///
/// Cada ciclo abre o proprio escopo de injecao. Sem isso, o
/// <c>DbContext</c> — que tem escopo de requisicao — viveria pelo tempo do
/// processo inteiro, acumulando entidades rastreadas ate consumir a memoria da
/// maquina.
///
/// Uma falha em um ciclo nunca derruba o laco: ela e registrada e o proximo
/// ciclo tenta de novo. Um processo de fundo que morre na primeira falha de
/// rede deixa a fila crescendo em silencio.
/// </summary>
public abstract partial class LacoDeSegundoPlano : BackgroundService
{
    private static readonly Meter Medidor = new(Telemetria.MedidorDeMensageria);

    /// <summary>
    /// Ciclos que terminaram em excecao, por laco.
    ///
    /// O laco continua rodando depois de uma falha — e justamente por isso ela
    /// precisa de contador. Sem metrica, um despachante que falha em todo ciclo
    /// se parece, de fora, com um despachante ocioso: nada quebra, nada alerta,
    /// e os eventos simplesmente nao saem.
    /// </summary>
    private static readonly Counter<long> Falhas =
        Medidor.CreateCounter<long>(Telemetria.Instrumentos.FalhasDeLaco);

    private readonly IServiceScopeFactory _escopos;
    private readonly OpcoesDeSegundoPlano _opcoes;
    private readonly ILogger _log;

    protected LacoDeSegundoPlano(
        IServiceScopeFactory escopos,
        OpcoesDeSegundoPlano opcoes,
        ILogger log)
    {
        _escopos = escopos;
        _opcoes = opcoes;
        _log = log;
    }

    protected abstract string Nome { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opcoes.Habilitado)
        {
            RegistrarDesligado(_log, Nome);
            return;
        }

        RegistrarInicio(_log, Nome);

        while (!stoppingToken.IsCancellationRequested)
        {
            var trabalhou = false;

            try
            {
                using var escopo = _escopos.CreateScope();

                trabalhou = await ExecutarCicloAsync(escopo.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception excecao)
            {
                Falhas.Add(1, new KeyValuePair<string, object?>("laco", Nome));
                RegistrarFalhaNoCiclo(_log, Nome, excecao);
            }

            var espera = trabalhou ? _opcoes.IntervaloAtivoEmMs : _opcoes.IntervaloOciosoEmMs;

            try
            {
                await Task.Delay(espera, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        RegistrarParada(_log, Nome);
    }

    /// <summary>Devolve <c>true</c> quando houve trabalho neste ciclo.</summary>
    protected abstract Task<bool> ExecutarCicloAsync(
        IServiceProvider servicos,
        CancellationToken cancellationToken);

    [LoggerMessage(
        EventId = 520,
        Level = LogLevel.Information,
        Message = "Laco de fundo {Nome} desligado por configuracao.")]
    private static partial void RegistrarDesligado(ILogger logger, string nome);

    [LoggerMessage(EventId = 521, Level = LogLevel.Information, Message = "Laco de fundo {Nome} iniciado.")]
    private static partial void RegistrarInicio(ILogger logger, string nome);

    [LoggerMessage(EventId = 522, Level = LogLevel.Information, Message = "Laco de fundo {Nome} encerrado.")]
    private static partial void RegistrarParada(ILogger logger, string nome);

    [LoggerMessage(
        EventId = 523,
        Level = LogLevel.Error,
        Message = "Falha no ciclo do laco {Nome}. O laco continua.")]
    private static partial void RegistrarFalhaNoCiclo(ILogger logger, string nome, Exception excecao);
}

/// <summary>Publica o que esta pendente na Outbox.</summary>
public sealed class LacoDoDespachante : LacoDeSegundoPlano
{
    public LacoDoDespachante(
        IServiceScopeFactory escopos,
        OpcoesDeSegundoPlano opcoes,
        ILogger<LacoDoDespachante> log)
        : base(escopos, opcoes, log)
    {
    }

    protected override string Nome => "despachante-da-outbox";

    protected override async Task<bool> ExecutarCicloAsync(
        IServiceProvider servicos,
        CancellationToken cancellationToken)
    {
        var resultado = await servicos
            .GetRequiredService<DespachanteDeEventos>()
            .DespacharLoteAsync(cancellationToken);

        // A amostragem de profundidade pega carona neste laco em vez de ter um
        // laco proprio: ele ja abre escopo, ja tem conexao com o banco e ja
        // roda no ritmo certo. Um quarto processo de fundo so para medir seria
        // maquina a mais para o mesmo resultado.
        //
        // Depois do despacho, e nao antes: medir a Outbox um instante antes de
        // esvazia-la produziria um numero que descreve o passado imediato e
        // sugere um atraso que acabou de ser resolvido.
        await servicos
            .GetRequiredService<AmostradorDeIndicadores>()
            .AmostrarSeVencidoAsync(cancellationToken);

        return resultado.Total > 0;
    }
}

/// <summary>
/// Consome a fila de backtests.
///
/// Laco separado, e nao um segundo tipo de mensagem no laco operacional: um
/// backtest de milhares de transacoes ocuparia o ciclo inteiro, e o alerta de
/// uma transacao bloqueada ficaria esperando atras dele (CLAUDE.md secao 30).
/// Na Fase 14 os dois viram Lambdas diferentes, com gatilhos diferentes — e o
/// codigo de dentro do ciclo nao muda.
/// </summary>
public sealed class LacoDeBacktests : LacoDeSegundoPlano
{
    public LacoDeBacktests(
        IServiceScopeFactory escopos,
        OpcoesDeSegundoPlano opcoes,
        ILogger<LacoDeBacktests> log)
        : base(escopos, opcoes, log)
    {
    }

    protected override string Nome => "executor-de-backtests";

    protected override async Task<bool> ExecutarCicloAsync(
        IServiceProvider servicos,
        CancellationToken cancellationToken)
    {
        var resultado = await servicos
            .GetRequiredService<ProcessadorDeBacktests>()
            .ConsumirLoteAsync(cancellationToken);

        return resultado.Total > 0;
    }
}

/// <summary>Consome a fila operacional.</summary>
public sealed class LacoDoConsumidor : LacoDeSegundoPlano
{
    public LacoDoConsumidor(
        IServiceScopeFactory escopos,
        OpcoesDeSegundoPlano opcoes,
        ILogger<LacoDoConsumidor> log)
        : base(escopos, opcoes, log)
    {
    }

    protected override string Nome => "consumidor-de-eventos";

    protected override async Task<bool> ExecutarCicloAsync(
        IServiceProvider servicos,
        CancellationToken cancellationToken)
    {
        var resultado = await servicos
            .GetRequiredService<ProcessadorDeEventos>()
            .ConsumirLoteAsync(cancellationToken);

        return resultado.Total > 0;
    }
}
