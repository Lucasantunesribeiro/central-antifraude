using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Integracoes;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Application.Risco;

/// <summary>Uma transacao com a avaliacao que ela recebeu.</summary>
public sealed record TransacaoAvaliada(Transacao Transacao, AvaliacaoDeRisco? Avaliacao);

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

    public ServicoDeConsultaDeRisco(IRepositorioDeTransacoes transacoes, IRepositorioDeRisco risco)
    {
        _transacoes = transacoes;
        _risco = risco;
    }

    public async Task<Pagina<TransacaoAvaliada>> ListarTransacoesAsync(
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken)
    {
        var pagina = await _transacoes.ListarAsync(paginacao, ordenacao, cancellationToken);

        var avaliacoes = await _risco.BuscarAvaliacoesPorTransacoesAsync(
            pagina.Itens.Select(t => t.Id).ToList(),
            cancellationToken);

        var itens = pagina.Itens
            .Select(t => new TransacaoAvaliada(
                t,
                avaliacoes.TryGetValue(t.Id, out var avaliacao) ? avaliacao : null))
            .ToList();

        return new Pagina<TransacaoAvaliada>(itens, paginacao, pagina.TotalDeItens);
    }

    /// <summary>
    /// Detalhe de uma transacao.
    ///
    /// Transacao de outro tenant nao existe daqui: o filtro global nao a
    /// devolve e a resposta e 404, sem revelar que o identificador e valido em
    /// outro lugar (CLAUDE.md secao 52).
    /// </summary>
    public async Task<TransacaoAvaliada> ObterTransacaoAsync(
        Guid transacaoId,
        CancellationToken cancellationToken)
    {
        var transacao = await _transacoes.BuscarPorIdAsync(transacaoId, cancellationToken)
            ?? throw new RecursoNaoEncontrado("Transacao");

        var avaliacao = await _risco.BuscarAvaliacaoPorTransacaoAsync(transacaoId, cancellationToken);

        return new TransacaoAvaliada(transacao, avaliacao);
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
