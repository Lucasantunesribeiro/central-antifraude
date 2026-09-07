using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Integracoes;
using CentralAntifraude.Application.Operacao;
using CentralAntifraude.Application.Transacoes;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Application.Risco;

/// <summary>Uma transacao com a avaliacao que ela recebeu.</summary>
public sealed record TransacaoAvaliada(Transacao Transacao, AvaliacaoDeRisco? Avaliacao);

/// <summary>
/// A transacao com tudo o que a tela de detalhe precisa: a avaliacao que
/// explica a decisao e o que a operacao fez depois.
///
/// As duas metades respondem perguntas diferentes — "por que esta decisao" e
/// "alguem ja olhou isto" — e o ROADMAP 10.3 pede as duas na mesma tela.
/// </summary>
public sealed record TransacaoCompleta(
    TransacaoAvaliada Avaliada,
    ContextoOperacionalDaTransacao Contexto);

/// <summary>
/// Leitura das telas operacionais.
///
/// Junta transacao e avaliacao em um lugar so para que o endpoint nao precise
/// saber que sao duas consultas — e para que a juncao seja feita uma vez por
/// pagina, e nao uma vez por linha.
///
/// <see cref="TransacaoAvaliada.Avaliacao"/> e anulavel de proposito. Toda
/// transacao ingerida a partir da Fase 3 nasce com avaliacao na mesma
/// gravacao, mas as transacoes gravadas na Fase 2 existem sem ela. Mostrar
/// "sem avaliacao" e honesto; fingir score zero seria inventar uma decisao que
/// ninguem tomou.
/// </summary>
public sealed class ServicoDeConsultaDeRisco
{
    private readonly IRepositorioDeTransacoes _transacoes;
    private readonly IRepositorioDeRisco _risco;
    private readonly IRepositorioDeOperacao _operacao;

    public ServicoDeConsultaDeRisco(
        IRepositorioDeTransacoes transacoes,
        IRepositorioDeRisco risco,
        IRepositorioDeOperacao operacao)
    {
        _transacoes = transacoes;
        _risco = risco;
        _operacao = operacao;
    }

    public Task<Pagina<TransacaoAvaliada>> ListarTransacoesAsync(
        FiltroDeTransacoes filtro,
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken) =>
        _transacoes.ListarComAvaliacaoAsync(filtro, paginacao, ordenacao, cancellationToken);

    /// <summary>
    /// Detalhe de uma transacao.
    ///
    /// Transacao de outro tenant nao existe daqui: o filtro global nao a
    /// devolve e a resposta e 404, sem revelar que o identificador e valido em
    /// outro lugar (CLAUDE.md secao 52).
    /// </summary>
    public async Task<TransacaoCompleta> ObterTransacaoAsync(
        Guid transacaoId,
        CancellationToken cancellationToken)
    {
        var transacao = await _transacoes.BuscarPorIdAsync(transacaoId, cancellationToken)
            ?? throw new RecursoNaoEncontrado("Transacao");

        var avaliacao = await _risco.BuscarAvaliacaoPorTransacaoAsync(transacaoId, cancellationToken);

        // O contexto operacional so e carregado depois de a transacao existir
        // no tenant. Carrega-lo antes daria a um identificador de outra
        // organizacao uma consulta a mais para produzir o mesmo 404.
        var contexto = await _operacao.CarregarContextoDaTransacaoAsync(
            transacaoId,
            cancellationToken);

        return new TransacaoCompleta(new TransacaoAvaliada(transacao, avaliacao), contexto);
    }

    /// <summary>
    /// Regras vigentes do tenant, com a versao que esta no perfil ativo.
    ///
    /// Somente leitura. A gestao de regras — rascunho, backtest, publicacao —
    /// e a Fase 8; ate la o catalogo e o provisionado com a organizacao, e
    /// nao ha endpoint que o altere.
    /// </summary>
    public Task<IReadOnlyList<(Regra Regra, VersaoDeRegra Versao)>> ListarRegrasAsync(
        CancellationToken cancellationToken) =>
        _risco.ListarRegrasVigentesAsync(cancellationToken);

    /// <summary>Versao de perfil que vale agora, com os limiares em vigor.</summary>
    public Task<VersaoDePerfilDeRisco?> ObterPerfilVigenteAsync(CancellationToken cancellationToken) =>
        _risco.BuscarVersaoAtivaDoPerfilAsync(cancellationToken);
}
