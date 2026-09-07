using CentralAntifraude.Domain.Tempo;

namespace CentralAntifraude.Application.Operacao;

/// <summary>
/// O painel operacional e as metricas de regra.
///
/// **Uma unica fonte para cada numero.** As decisoes do painel sao contadas
/// direto das avaliacoes, e nao da projecao diaria que a Fase 5 criou. Duas
/// fontes para a mesma pergunta podem discordar — a projecao e alimentada pelo
/// caminho assincrono e fica para tras quando a fila atrasa — e um painel que
/// discorda da lista de transacoes gera investigacao sobre um problema que nao
/// existe (CLAUDE.md secao 44).
///
/// A projecao continua com o papel que sempre teve: ser o efeito assincrono
/// que prova a idempotencia do consumidor ponta a ponta. O que o painel expoe
/// no lugar dela e algo que ela nao responde — quantos eventos ainda nao
/// sairam da Outbox, que e a medida direta de fila parada.
///
/// **Todo numero e do tenant atual.** Nenhuma consulta aqui desliga o filtro
/// global: o painel roda dentro de uma requisicao autenticada, e um painel que
/// somasse organizacoes seria o vazamento mais silencioso possivel — numeros
/// plausiveis, sem nada que denunciasse.
/// </summary>
public sealed class ServicoDoPainel
{
    /// <summary>Quantos tipos de sinal aparecem na lista dos mais frequentes.</summary>
    public const int SinaisNoPainel = 5;

    /// <summary>
    /// A partir de quantos dias um caso aberto conta como antigo.
    ///
    /// Tres dias e escolha de demonstracao deste projeto, e nao padrao de
    /// mercado (CLAUDE.md secao 112). O numero existe para que "casos
    /// antigos" seja uma contagem definida, e nao uma impressao.
    /// </summary>
    public const int DiasParaCasoAntigo = 3;

    private readonly IRepositorioDeOperacao _operacao;
    private readonly IRelogio _relogio;

    public ServicoDoPainel(IRepositorioDeOperacao operacao, IRelogio relogio)
    {
        _operacao = operacao;
        _relogio = relogio;
    }

    public async Task<ResumoOperacional> ResumirAsync(
        JanelaDoPainel janela,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(janela);

        var decisoes = await _operacao.ContarDecisoesAsync(janela, cancellationToken);

        return new ResumoOperacional(
            janela,
            await _operacao.ContarTransacoesRecebidasAsync(janela, cancellationToken),
            decisoes.Sum(d => d.Quantidade),
            decisoes,
            await _operacao.ContarDecisoesPorDiaAsync(janela, cancellationToken),
            await _operacao.ContarSinaisAsync(janela, SinaisNoPainel, cancellationToken),
            await _operacao.ContarAlertasAbertosAsync(cancellationToken),
            await _operacao.ContarCasosAsync(cancellationToken),
            await _operacao.ContarCasosAntigosAsync(
                _relogio.Agora.AddDays(-DiasParaCasoAntigo),
                cancellationToken),
            await _operacao.ContarEventosPendentesAsync(cancellationToken));
    }

    /// <summary>
    /// Acionamentos por regra no periodo, cruzados com o veredito humano.
    ///
    /// A leitura honesta esta no contrato de <see cref="MetricaDeRegra"/>: sao
    /// contagens com denominador, e nao nota de qualidade da regra.
    /// </summary>
    public Task<IReadOnlyList<MetricaDeRegra>> ApurarMetricasDeRegraAsync(
        JanelaDoPainel janela,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(janela);

        return _operacao.ApurarMetricasDeRegraAsync(janela, cancellationToken);
    }
}
