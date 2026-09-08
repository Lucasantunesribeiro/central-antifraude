using System.Diagnostics;
using System.Diagnostics.Metrics;
using CentralAntifraude.Application.Backtests;
using CentralAntifraude.Application.Correlacao;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Application.Observabilidade;
using CentralAntifraude.Domain.Backtests;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Tempo;
using Microsoft.Extensions.Logging;

namespace CentralAntifraude.Infrastructure.Mensageria;

/// <summary>
/// O consumidor da fila de backtests.
///
/// **A idempotencia aqui nao vem da Inbox, e a razao e melhor.** O que a Inbox
/// faria — lembrar que o evento X ja foi tratado — o proprio registro da
/// execucao ja faz, e com mais precisao: o <see cref="StatusDoBacktest"/> diz
/// se o trabalho terminou, e o token de versao arbitra dois workers que
/// tentem termina-lo ao mesmo tempo. E a "invariante propria do efeito" que o
/// CLAUDE.md secao 42 pede ao lado da Inbox — so que aqui ela e suficiente
/// sozinha, porque o efeito e uma unica linha e nao um conjunto de gravacoes
/// espalhadas.
///
/// **Cancelar nao envia sinal nenhum ao worker.** Quem cancela sobe a versao
/// da linha; este processo tenta concluir com a versao que leu e o banco
/// recusa. Isso fecha a janela em que um worker "quase la" gravaria o
/// resultado depois do cancelamento, e evita uma consulta de verificacao a
/// cada transacao analisada.
///
/// **Retomar uma execucao presa e seguro porque um backtest nao tem efeito
/// colateral** (ROADMAP 9.5): refazer o trabalho desperdica tempo e nada mais.
/// Se ele criasse alerta ou avaliacao, esta porta nao poderia existir.
/// </summary>
public sealed partial class ProcessadorDeBacktests
{
    private static readonly Meter Medidor = new(Telemetria.MedidorDeMensageria);

    /// <summary>
    /// Quanto tempo uma execucao de backtest levou.
    ///
    /// Fica longe da distribuicao do caminho critico de proposito: sao ordens
    /// de grandeza diferentes — milissegundos contra dezenas de segundos — e
    /// misturar as duas produziria um percentil que nao descreve nenhum dos
    /// dois. E tambem o numero que diz se a fila separada da Fase 9 continua
    /// fazendo sentido.
    /// </summary>
    private static readonly Histogram<double> Duracao =
        Medidor.CreateHistogram<double>(Telemetria.Instrumentos.BacktestDuracao);

    private readonly IRepositorioDeBacktests _backtests;
    private readonly ExecutorDeBacktest _executor;
    private readonly IUnidadeDeTrabalho _unidadeDeTrabalho;
    private readonly IFilaDeMensagens _fila;
    private readonly OpcoesDeBacktest _opcoes;
    private readonly IRelogio _relogio;
    private readonly ContextoDeCorrelacaoMutavel _correlacao;
    private readonly ILogger<ProcessadorDeBacktests> _log;

    public ProcessadorDeBacktests(
        IRepositorioDeBacktests backtests,
        ExecutorDeBacktest executor,
        IUnidadeDeTrabalho unidadeDeTrabalho,
        IFilaDeMensagens fila,
        OpcoesDeBacktest opcoes,
        IRelogio relogio,
        ContextoDeCorrelacaoMutavel correlacao,
        ILogger<ProcessadorDeBacktests> log)
    {
        _backtests = backtests;
        _executor = executor;
        _unidadeDeTrabalho = unidadeDeTrabalho;
        _fila = fila;
        _opcoes = opcoes;
        _relogio = relogio;
        _correlacao = correlacao;
        _log = log;
    }

    public async Task<ResultadoDoConsumo> ConsumirLoteAsync(CancellationToken cancellationToken)
    {
        // Uma mensagem por ciclo, e nao o lote inteiro da fila operacional:
        // cada backtest e trabalho longo, e pegar dez de uma vez esconderia
        // nove atras da visibilidade enquanto o primeiro roda.
        var mensagens = await _fila.ReceberAsync(
            IFilaDeMensagens.FilaDeBacktests,
            1,
            cancellationToken);

        var processados = 0;
        var repetidos = 0;
        var recusados = 0;
        var falhados = 0;

        foreach (var mensagem in mensagens)
        {
            switch (await TratarAsync(mensagem, cancellationToken))
            {
                case Desfecho.Concluido:
                    processados++;
                    break;
                case Desfecho.NadaAFazer:
                    repetidos++;
                    break;
                case Desfecho.Recusado:
                    recusados++;
                    break;
                default:
                    falhados++;
                    break;
            }
        }

        if (mensagens.Count > 0)
        {
            RegistrarCiclo(_log, processados, repetidos, recusados, falhados);
        }

        return new ResultadoDoConsumo(processados, repetidos, recusados, falhados);
    }

    private async Task<Desfecho> TratarAsync(
        MensagemRecebida mensagem,
        CancellationToken cancellationToken)
    {
        EnvelopeDeEvento envelope;

        try
        {
            envelope = SerializadorDeEnvelope.Desserializar(mensagem.Corpo);
        }
        catch (EnvelopeInvalido excecao)
        {
            RegistrarMensagemInvalida(_log, mensagem.RecebimentosAproximados, excecao.Message);

            return await DevolverParaRedriveAsync(mensagem, cancellationToken);
        }

        // Mesmo motivo do consumidor operacional: o backtest foi PEDIDO por
        // alguem, em uma requisicao que tem correlacao, e o registro do que ele
        // fez precisa continuar naquele fio.
        if (!string.IsNullOrWhiteSpace(envelope.CorrelationId))
        {
            _correlacao.Definir(envelope.CorrelationId);
        }

        using var escopoDeLog = _log.BeginScope(
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["CorrelationId"] = envelope.CorrelationId,
                ["EventId"] = envelope.EventId,
                ["EventType"] = envelope.TipoComposto,
            });

        if (envelope.Conteudo is not BacktestSolicitadoV1 conteudo)
        {
            // Mensagem de outro tipo na fila de backtests. Nao e para este
            // consumidor, e apagar em silencio esconderia um erro de
            // roteamento — a DLQ existe para guardar isto.
            RegistrarTipoInesperado(_log, envelope.TipoComposto, mensagem.RecebimentosAproximados);

            return await DevolverParaRedriveAsync(mensagem, cancellationToken);
        }

        var execucao = await _backtests.BuscarParaExecucaoAsync(conteudo.ExecucaoId, cancellationToken);

        // O tenant do envelope e uma AFIRMACAO de quem enviou. Conferir contra
        // o banco separa "evento nosso" de mensagem forjada: uma que declare o
        // tenant A e aponte para uma execucao do tenant B rodaria o backtest
        // de um cliente e o registraria como se fosse de outro.
        if (execucao is null || execucao.OrganizacaoId != envelope.TenantId)
        {
            RegistrarExecucaoInconsistente(
                _log,
                conteudo.ExecucaoId,
                mensagem.RecebimentosAproximados);

            return await DevolverParaRedriveAsync(mensagem, cancellationToken);
        }

        if (execucao.EstaEncerrada)
        {
            // Reentrega de algo que ja terminou — ou foi cancelado antes de o
            // worker chegar nela. Caminho normal, nao erro.
            await _fila.ApagarAsync(mensagem.Recibo, cancellationToken);

            return Desfecho.NadaAFazer;
        }

        return await AssumirEExecutarAsync(mensagem, execucao, envelope, cancellationToken);
    }

    private async Task<Desfecho> AssumirEExecutarAsync(
        MensagemRecebida mensagem,
        ExecucaoDeBacktest execucao,
        EnvelopeDeEvento envelope,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;

        if (execucao.Status == StatusDoBacktest.Executando)
        {
            var desde = agora - (execucao.IniciadaEm ?? agora);

            if (desde < _opcoes.TempoMaximoDeExecucao)
            {
                // Outro worker esta nela. Devolver deixa a mensagem voltar
                // depois; se aquele worker morreu, a proxima entrega ja
                // estara alem do tempo maximo e esta execucao sera retomada.
                await _fila.DevolverAsync(mensagem.Recibo, cancellationToken);

                return Desfecho.NadaAFazer;
            }

            execucao.Retomar(agora);
        }
        else
        {
            execucao.Iniciar(agora);
        }

        try
        {
            await _unidadeDeTrabalho.SalvarAsync(cancellationToken);
        }
        catch (ConflitoDeEstado)
        {
            // Dois workers pegaram a mesma execucao. Quem perde o token de
            // versao simplesmente sai — sem apagar a mensagem, porque quem
            // venceu vai apaga-la ao terminar.
            RegistrarDisputa(_log, execucao.Id);

            return Desfecho.NadaAFazer;
        }

        return await ApurarEFecharAsync(mensagem, execucao, envelope, cancellationToken);
    }

    private async Task<Desfecho> ApurarEFecharAsync(
        MensagemRecebida mensagem,
        ExecucaoDeBacktest execucao,
        EnvelopeDeEvento envelope,
        CancellationToken cancellationToken)
    {
        ResultadoDoBacktest resultado;
        var inicio = Stopwatch.GetTimestamp();

        try
        {
            resultado = await _executor.ApurarAsync(execucao, cancellationToken);
        }
        catch (Exception excecao) when (EhFalhaDefinitiva(excecao))
        {
            // Limite estourado ou dado que sumiu. Repetir daria o mesmo
            // resultado, entao a execucao termina com o motivo registrado —
            // que e o que a tela mostra a quem pediu.
            return await FecharComFalhaAsync(mensagem, execucao, excecao, cancellationToken);
        }
        catch (Exception excecao) when (excecao is not OperationCanceledException)
        {
            // Falha possivelmente transitoria: banco indisponivel, conexao
            // derrubada. A mensagem NAO e apagada e a execucao fica em
            // andamento; passado o tempo maximo, outro worker a retoma.
            RegistrarFalhaTransitoria(_log, execucao.Id, excecao);

            return Desfecho.Falhou;
        }

        // Registrada aqui, e nao depois do commit: o que se quer medir e o
        // custo de apurar o backtest, e nao o tempo de gravar uma linha. Uma
        // execucao cancelada no meio nao entra na distribuicao — ela nao chegou
        // ao fim, e incluir seu tempo parcial melhoraria o percentil por um
        // motivo que ninguem comemoraria.
        Duracao.Record(Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);

        execucao.Concluir(resultado, _relogio.Agora);

        try
        {
            await _unidadeDeTrabalho.SalvarAsync(cancellationToken);
        }
        catch (ConflitoDeEstado)
        {
            // Alguem cancelou enquanto isto rodava. O resultado e descartado
            // de proposito: gravar por cima transformaria "cancelei" em
            // "cancelei, mas o numero apareceu depois".
            RegistrarCanceladaDurante(_log, execucao.Id);

            await _fila.ApagarAsync(mensagem.Recibo, cancellationToken);

            return Desfecho.NadaAFazer;
        }

        await _fila.ApagarAsync(mensagem.Recibo, cancellationToken);

        // O fio da correlacao chega ate aqui (CLAUDE.md secao 69): o
        // identificador que voltou no `POST` encontra a linha que fechou a
        // execucao, em outro processo, minutos depois.
        RegistrarConclusao(
            _log,
            execucao.Id,
            envelope.CorrelationId,
            resultado.TotalAnalisado,
            resultado.TotalDeMudancas);

        return Desfecho.Concluido;
    }

    private async Task<Desfecho> FecharComFalhaAsync(
        MensagemRecebida mensagem,
        ExecucaoDeBacktest execucao,
        Exception excecao,
        CancellationToken cancellationToken)
    {
        RegistrarFalhaDefinitiva(_log, execucao.Id, excecao.Message);

        execucao.Falhar(excecao.Message, _relogio.Agora);

        try
        {
            await _unidadeDeTrabalho.SalvarAsync(cancellationToken);
        }
        catch (ConflitoDeEstado)
        {
            // Cancelada durante a execucao. Cancelada vence falha: foi decisao
            // de gente.
            RegistrarCanceladaDurante(_log, execucao.Id);
        }

        await _fila.ApagarAsync(mensagem.Recibo, cancellationToken);

        return Desfecho.Recusado;
    }

    /// <summary>
    /// Falhas que nao adianta repetir.
    ///
    /// Lista fechada de proposito: tudo o que nao esta aqui e tratado como
    /// transitorio e volta pela fila. Errar para o lado de repetir e melhor —
    /// um backtest repetido custa tempo, um backtest marcado como falho por
    /// causa de uma queda de rede custa uma decisao.
    /// </summary>
    private static bool EhFalhaDefinitiva(Exception excecao) =>
        excecao is BacktestExcedeuOTempo
            or BacktestExcedeuOVolume
            or BacktestExcedeuOContexto
            or Domain.ViolacaoDeInvariante
            or InvalidOperationException;

    private async Task<Desfecho> DevolverParaRedriveAsync(
        MensagemRecebida mensagem,
        CancellationToken cancellationToken)
    {
        // Nao se apaga o que nao se entende: a politica de redrive leva a
        // mensagem para a fila de mortas depois de
        // <c>MaximoDeRecebimentos</c> entregas, e ela fica la para analise.
        await _fila.DevolverAsync(mensagem.Recibo, cancellationToken);

        return Desfecho.Recusado;
    }

    private enum Desfecho
    {
        Concluido,
        NadaAFazer,
        Recusado,
        Falhou,
    }

    [LoggerMessage(
        EventId = 530,
        Level = LogLevel.Information,
        Message = "Backtests: {Processados} concluido(s), {Repetidos} sem trabalho, " +
                  "{Recusados} recusado(s), {Falhados} com falha.")]
    private static partial void RegistrarCiclo(
        ILogger logger,
        int processados,
        int repetidos,
        int recusados,
        int falhados);

    [LoggerMessage(
        EventId = 531,
        Level = LogLevel.Warning,
        Message = "Mensagem invalida na fila de backtests, entrega {Recebimentos}: {Motivo}.")]
    private static partial void RegistrarMensagemInvalida(
        ILogger logger,
        int recebimentos,
        string motivo);

    [LoggerMessage(
        EventId = 532,
        Level = LogLevel.Warning,
        Message = "Tipo {Tipo} na fila de backtests nao pertence a ela. Entrega {Recebimentos}.")]
    private static partial void RegistrarTipoInesperado(
        ILogger logger,
        string tipo,
        int recebimentos);

    [LoggerMessage(
        EventId = 533,
        Level = LogLevel.Warning,
        Message = "Execucao {ExecucaoId} inexistente ou de outro tenant. Entrega {Recebimentos}.")]
    private static partial void RegistrarExecucaoInconsistente(
        ILogger logger,
        Guid execucaoId,
        int recebimentos);

    [LoggerMessage(
        EventId = 534,
        Level = LogLevel.Information,
        Message = "Outro worker ja assumiu a execucao {ExecucaoId}.")]
    private static partial void RegistrarDisputa(ILogger logger, Guid execucaoId);

    // Nem o candidato nem o resultado entram no log: o candidato descreve a
    // estrategia antifraude do cliente e o resultado descreve o historico
    // dele. As contagens bastam para acompanhar.
    [LoggerMessage(
        EventId = 535,
        Level = LogLevel.Information,
        Message = "Backtest {ExecucaoId} concluido sobre {Analisadas} transacao(oes), " +
                  "{Mudancas} decisao(oes) mudariam. Correlacao {IdDeCorrelacao}.")]
    private static partial void RegistrarConclusao(
        ILogger logger,
        Guid execucaoId,
        string idDeCorrelacao,
        int analisadas,
        int mudancas);

    [LoggerMessage(
        EventId = 536,
        Level = LogLevel.Warning,
        Message = "Backtest {ExecucaoId} falhou definitivamente: {Motivo}.")]
    private static partial void RegistrarFalhaDefinitiva(
        ILogger logger,
        Guid execucaoId,
        string motivo);

    [LoggerMessage(
        EventId = 537,
        Level = LogLevel.Error,
        Message = "Falha ao executar o backtest {ExecucaoId}. A mensagem volta para a fila.")]
    private static partial void RegistrarFalhaTransitoria(
        ILogger logger,
        Guid execucaoId,
        Exception excecao);

    [LoggerMessage(
        EventId = 538,
        Level = LogLevel.Information,
        Message = "Backtest {ExecucaoId} foi cancelado durante a execucao. Resultado descartado.")]
    private static partial void RegistrarCanceladaDurante(ILogger logger, Guid execucaoId);
}
