using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Application.Integracoes;
using CentralAntifraude.Application.Risco;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Primitivos;
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
/// Recebe uma tentativa de pagamento e a registra exatamente uma vez.
///
/// A idempotencia tem tres camadas, de proposito (CLAUDE.md secao 33):
///
/// 1. **Consulta antes de inserir.** Resolve o caso comum — o retry que chega
///    segundos depois — sem gerar erro no banco.
/// 2. **Restricao unica no banco.** E a garantia de verdade. Duas requisicoes
///    simultaneas passam as duas pela consulta e acham nada; uma delas perde
///    no INSERT.
/// 3. **Nova consulta apos o conflito.** Quem perdeu a corrida encontra a
///    linha da vencedora e devolve o mesmo resultado.
///
/// So a camada 1 seria a armadilha classica do <c>SELECT</c> seguido de
/// <c>INSERT</c>, que o CLAUDE.md secao 33 proibe explicitamente: sob
/// concorrencia ela cria duplicata. So a camada 2 devolveria 500 em vez do
/// resultado correto.
/// </summary>
public sealed class ServicoDeIngestao
{
    private readonly IRepositorioDeTransacoes _transacoes;
    private readonly IContextoDaIntegracaoAtual _integracao;
    private readonly IFingerprintDeIp _fingerprintDeIp;
    private readonly IUnidadeDeTrabalho _unidadeDeTrabalho;
    private readonly ServicoDeAvaliacaoDeRisco _avaliacao;
    private readonly IRepositorioDeRisco _risco;
    private readonly IRelogio _relogio;
    private readonly OpcoesDeIngestao _opcoes;

    public ServicoDeIngestao(
        IRepositorioDeTransacoes transacoes,
        IContextoDaIntegracaoAtual integracao,
        IFingerprintDeIp fingerprintDeIp,
        IUnidadeDeTrabalho unidadeDeTrabalho,
        ServicoDeAvaliacaoDeRisco avaliacao,
        IRepositorioDeRisco risco,
        IRelogio relogio,
        OpcoesDeIngestao opcoes)
    {
        _transacoes = transacoes;
        _integracao = integracao;
        _fingerprintDeIp = fingerprintDeIp;
        _unidadeDeTrabalho = unidadeDeTrabalho;
        _avaliacao = avaliacao;
        _risco = risco;
        _relogio = relogio;
        _opcoes = opcoes;
    }

    public async Task<ResultadoDaIngestao> RegistrarAsync(
        ConteudoDaTransacao conteudo,
        string? chaveDeIdempotencia,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conteudo);

        var agora = _relogio.Agora;

        ValidadorDaTransacao.Validar(conteudo, chaveDeIdempotencia, agora, _opcoes);

        var chave = chaveDeIdempotencia!.Trim();
        var fingerprint = FingerprintDaRequisicao.Calcular(conteudo);

        // Camada 1.
        if (await LocalizarEquivalenteAsync(conteudo, chave, fingerprint, cancellationToken) is { } jaRegistrada)
        {
            return await ReplicarResultadoAsync(jaRegistrada, cancellationToken);
        }

        var transacao = Montar(conteudo, chave, fingerprint, agora);
        _transacoes.Adicionar(transacao);

        // A avaliacao entra na MESMA gravacao da transacao. Uma transacao
        // registrada sem avaliacao seria um estado que nenhuma tela sabe
        // mostrar e que nenhuma regra sabe corrigir depois.
        var avaliacao = await _avaliacao.AvaliarAsync(transacao, cancellationToken);

        try
        {
            // Camada 2.
            await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

            return new ResultadoDaIngestao(transacao, avaliacao, JaExistia: false);
        }
        catch (ConflitoDeUnicidadeNoBanco)
        {
            // Camada 3: outra requisicao com a mesma chave venceu a corrida
            // entre a consulta e o INSERT. A resposta correta e a dela -
            // inclusive a avaliacao, que NAO e recalculada aqui.
            var vencedora = await LocalizarEquivalenteAsync(conteudo, chave, fingerprint, cancellationToken);

            if (vencedora is not null)
            {
                return await ReplicarResultadoAsync(vencedora, cancellationToken);
            }

            // Restricao violada sem que a linha correspondente exista: nao ha
            // caminho conhecido que produza isso. Deixar subir como falha
            // interna e mais honesto do que inventar uma resposta.
            throw;
        }
    }

    /// <summary>
    /// Devolve o resultado ORIGINAL de uma transacao ja registrada.
    ///
    /// A avaliacao e lida do banco, nunca recalculada. Recalcular faria o
    /// mesmo pedido receber decisoes diferentes conforme as regras mudassem
    /// entre a primeira tentativa e o retry - e o integrador nao teria como
    /// saber qual das duas vale.
    ///
    /// A Fase 4 transforma isto em invariante testada explicitamente
    /// (ROADMAP secao 4.6).
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
            fingerprint);
}
