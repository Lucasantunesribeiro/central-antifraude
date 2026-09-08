using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Correlacao;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Eventos;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Application.Integracoes;
using CentralAntifraude.Application.Risco;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Tempo;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Application.Transacoes;

/// <summary>
/// Resultado de uma ingestao.
///
/// <see cref="JaExistia"/> distingue "criei agora" (HTTP 201) de "isto ja
/// tinha sido registrado" (HTTP 200). O integrador precisa dessa diferenca
/// para saber se o retry dele foi necessario.
/// </summary>
public sealed record ResultadoDaIngestao(
    Transacao Transacao,
    AvaliacaoDeRisco Avaliacao,
    bool JaExistia);

/// <summary>
/// A operacao critica da Central Antifraude: recebe uma tentativa de
/// pagamento, registra exatamente uma vez, avalia o risco e devolve a decisao.
///
/// Tudo isso acontece **sincronamente e em uma unica transacao serializavel**
/// (CLAUDE.md secao 28). O cliente precisa de `Permitir`, `Revisar` ou
/// `Bloquear` na propria resposta — colocar uma fila entre a requisicao e a
/// decisao inverteria o produto.
///
/// **A validacao fica de fora da transacao**, de proposito. Ela e
/// deterministica e nao toca no banco; abrir uma transacao serializavel para
/// recusar um payload malformado gastaria isolamento forte com quem nem chega
/// a competir por nada.
///
/// **A idempotencia tem tres camadas** (CLAUDE.md secao 33), e a terceira
/// muda de forma sob isolamento forte:
///
/// 1. **Consulta antes de inserir.** Resolve o caso comum — o retry que chega
///    segundos depois — sem gerar erro no banco.
/// 2. **Restricao unica no banco.** E a garantia de verdade. Duas requisicoes
///    simultaneas passam as duas pela consulta e acham nada; uma perde no
///    INSERT.
/// 3. **Refazer a operacao inteira.** Quem perdeu a corrida NAO consegue
///    encontrar a vencedora reconsultando dentro da mesma transacao: o
///    snapshot dela e anterior ao commit da outra, e a linha e invisivel. O
///    <see cref="IExecutorDeOperacaoCritica"/> aborta e refaz com um snapshot
///    novo, no qual a camada 1 acha a vencedora e devolve o resultado dela.
///
/// So a camada 1 seria a armadilha do <c>SELECT</c> seguido de <c>INSERT</c>
/// que o CLAUDE.md secao 33 proibe explicitamente. So a camada 2 devolveria
/// 500 em vez do resultado correto.
/// </summary>
public sealed class ServicoDeIngestao
{
    private readonly IRepositorioDeTransacoes _transacoes;
    private readonly IContextoDaIntegracaoAtual _integracao;
    private readonly IFingerprintDeIp _fingerprintDeIp;
    private readonly IUnidadeDeTrabalho _unidadeDeTrabalho;
    private readonly IExecutorDeOperacaoCritica _operacaoCritica;
    private readonly ServicoDeAvaliacaoDeRisco _avaliacao;
    private readonly IRepositorioDeRisco _risco;
    private readonly IRepositorioDeEventos _eventos;
    private readonly IContextoDeCorrelacao _correlacao;
    private readonly IRelogio _relogio;
    private readonly IDespachanteImediato _despachanteImediato;
    private readonly OpcoesDeIngestao _opcoes;

    public ServicoDeIngestao(
        IRepositorioDeTransacoes transacoes,
        IContextoDaIntegracaoAtual integracao,
        IFingerprintDeIp fingerprintDeIp,
        IUnidadeDeTrabalho unidadeDeTrabalho,
        IExecutorDeOperacaoCritica operacaoCritica,
        ServicoDeAvaliacaoDeRisco avaliacao,
        IRepositorioDeRisco risco,
        IRepositorioDeEventos eventos,
        IContextoDeCorrelacao correlacao,
        IRelogio relogio,
        IDespachanteImediato despachanteImediato,
        OpcoesDeIngestao opcoes)
    {
        _transacoes = transacoes;
        _integracao = integracao;
        _fingerprintDeIp = fingerprintDeIp;
        _unidadeDeTrabalho = unidadeDeTrabalho;
        _operacaoCritica = operacaoCritica;
        _avaliacao = avaliacao;
        _risco = risco;
        _eventos = eventos;
        _correlacao = correlacao;
        _relogio = relogio;
        _despachanteImediato = despachanteImediato;
        _opcoes = opcoes;
    }

    public async Task<ResultadoDaIngestao> RegistrarAsync(
        ConteudoDaTransacao conteudo,
        string? chaveDeIdempotencia,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conteudo);

        ValidadorDaTransacao.Validar(conteudo, chaveDeIdempotencia, _relogio.Agora, _opcoes);

        var chave = chaveDeIdempotencia!.Trim();
        var fingerprint = FingerprintDaRequisicao.Calcular(conteudo);

        // Daqui para baixo, tudo roda dentro do boundary transacional forte -
        // e pode rodar mais de uma vez. Por isso nada e capturado de fora: o
        // relogio e relido, o contexto historico e relido e a avaliacao e
        // refeita a cada tentativa.
        var resultado = await _operacaoCritica.ExecutarAsync(
            ct => ExecutarRegistroAsync(conteudo, chave, fingerprint, ct),
            cancellationToken);

        // DEPOIS do commit, e fora do boundary transacional.
        //
        // A posicao e a decisao. Dentro da transacao, o despachante poderia ler
        // a Outbox antes do commit e nao encontrar nada — ou pior, encontrar e
        // publicar um evento cuja transacao ainda pode ser desfeita por uma
        // falha de serializacao. Aqui, o evento existe e esta confirmado.
        //
        // Uma repeticao por retry de concorrencia tambem nao faz mal: acordar
        // o despachante duas vezes publica o mesmo evento uma vez so, porque
        // quem decide isso e o `FOR UPDATE SKIP LOCKED` da Fase 5.
        //
        // Nao ha `try` aqui: a propria implementacao engole a falha, e o
        // contrato diz isso. Um `try` neste ponto sugeriria que existe algo a
        // tratar, quando a decisao ja foi tomada uma camada abaixo.
        await _despachanteImediato.AcordarAsync(cancellationToken);

        return resultado;
    }

    private async Task<ResultadoDaIngestao> ExecutarRegistroAsync(
        ConteudoDaTransacao conteudo,
        string chave,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        // Camada 1.
        if (await LocalizarEquivalenteAsync(conteudo, chave, fingerprint, cancellationToken) is { } jaRegistrada)
        {
            return await ReplicarResultadoAsync(jaRegistrada, cancellationToken);
        }

        var transacao = Montar(conteudo, chave, fingerprint, _relogio.Agora);
        _transacoes.Adicionar(transacao);

        // A avaliacao entra na MESMA gravacao da transacao. Uma transacao
        // registrada sem avaliacao seria um estado que nenhuma tela sabe
        // mostrar e que nenhuma regra sabe corrigir depois.
        var avaliacao = await _avaliacao.AvaliarAsync(transacao, cancellationToken);

        // E o evento entra junto dos dois. Este e o ponto do Transactional
        // Outbox (CLAUDE.md secao 38): decisao e intencao de publicar sao
        // gravadas atomicamente, entao nao existe o estado "avaliei mas
        // ninguem nunca vai saber" nem "avisei sobre algo que nao existe".
        _eventos.Adicionar(EventoDeSaida.Registrar(
            transacao.OrganizacaoId,
            TransacaoAvaliadaV1.De(transacao, avaliacao),
            avaliacao.AvaliadaEm,
            _correlacao.IdDeCorrelacao));

        // Camada 2. Uma violacao aqui significa que outra requisicao venceu a
        // corrida; o executor aborta e refaz a operacao (camada 3).
        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return new ResultadoDaIngestao(transacao, avaliacao, JaExistia: false);
    }

    /// <summary>
    /// Devolve o resultado ORIGINAL de uma transacao ja registrada.
    ///
    /// A avaliacao e lida do banco, nunca recalculada. Recalcular faria o
    /// mesmo pedido receber decisoes diferentes conforme as regras mudassem
    /// entre a primeira tentativa e o retry — e o integrador nao teria como
    /// saber qual das duas vale (ROADMAP secao 4.6).
    ///
    /// O replay tambem NAO gera um evento novo. O fato "esta transacao foi
    /// avaliada" aconteceu uma vez so; publica-lo de novo criaria efeitos
    /// duplicados a cada retry do integrador.
    /// </summary>
    private async Task<ResultadoDaIngestao> ReplicarResultadoAsync(
        Transacao transacao,
        CancellationToken cancellationToken)
    {
        var avaliacao = await _risco.BuscarAvaliacaoPorTransacaoAsync(transacao.Id, cancellationToken)
            ?? throw new ConflitoDeEstado(
                "avaliacao_ausente",
                "A transacao existe sem avaliacao de risco associada.");

        return new ResultadoDaIngestao(transacao, avaliacao, JaExistia: true);
    }

    /// <summary>
    /// Procura uma transacao ja registrada para este pedido.
    ///
    /// Duas barreiras, e as duas comparam o fingerprint do conteudo:
    ///
    /// - **mesma chave de idempotencia** — o retry declarado pelo integrador;
    /// - **mesmo identificador externo** — a barreira de negocio, que pega o
    ///   caso em que o integrador gerou uma chave nova para a mesma tentativa
    ///   de pagamento (CLAUDE.md secao 33).
    ///
    /// Conteudo diferente com o mesmo identificador e conflito, nunca
    /// sobrescrita: uma transacao registrada nao muda depois.
    /// </summary>
    private async Task<Transacao?> LocalizarEquivalenteAsync(
        ConteudoDaTransacao conteudo,
        string chave,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var porChave = await _transacoes.BuscarPorChaveDeIdempotenciaAsync(
            _integracao.IntegracaoId,
            chave,
            cancellationToken);

        if (porChave is not null)
        {
            return porChave.FingerprintDoPayload == fingerprint
                ? porChave
                : throw new ConflitoDeEstado(
                    "idempotencia_conflitante",
                    "Esta chave de idempotencia ja foi usada com um conteudo diferente. " +
                    "Use uma chave nova para uma tentativa de pagamento diferente.");
        }

        var porIdentificador = await _transacoes.BuscarPorIdentificadorExternoAsync(
            _integracao.IntegracaoId,
            conteudo.IdentificadorExterno!.Trim(),
            cancellationToken);

        if (porIdentificador is null)
        {
            return null;
        }

        return porIdentificador.FingerprintDoPayload == fingerprint
            ? porIdentificador
            : throw new ConflitoDeEstado(
                "transacao_externa_conflitante",
                "Ja existe uma transacao com este identificadorExterno e conteudo diferente. " +
                "Uma transacao registrada nao pode ser alterada.");
    }

    private Transacao Montar(
        ConteudoDaTransacao conteudo,
        string chave,
        string fingerprint,
        DateTimeOffset agora) =>
        Transacao.Registrar(
            _integracao.OrganizacaoId,
            _integracao.IntegracaoId,
            conteudo.IdentificadorExterno!,
            Dinheiro.De(conteudo.Valor, conteudo.Moeda!),
            conteudo.OcorridaEm,
            // RecebidaEm e cravado aqui, pelo relogio do servidor. Deixar a
            // integracao informa-lo permitiria mascarar um atraso — e e
            // justamente a diferenca entre os dois tempos que denuncia evento
            // atrasado (CLAUDE.md secao 15).
            recebidaEm: agora,
            conteudo.ClienteExternoId!,
            conteudo.ReferenciaDoInstrumento!,
            conteudo.FingerprintDoDispositivo,
            // O IP entra so como HMAC. O endereco em si nao chega ao banco.
            _fingerprintDeIp.Derivar(conteudo.EnderecoIp),
            conteudo.PaisDeOrigem,
            chave,
            fingerprint,
            // A correlacao da requisicao fica na transacao (CLAUDE.md secao
            // 69). Ela nao entra no fingerprint do payload: identifica a
            // requisicao, e nao o conteudo — incluir faria dois envios
            // identicos parecerem diferentes.
            _correlacao.IdDeCorrelacao);
}
