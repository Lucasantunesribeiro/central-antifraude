using CentralAntifraude.Application.Backtests;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Domain.Backtests;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.Infrastructure.Persistencia.Repositorios;

/// <summary>
/// Acesso as execucoes de backtest.
///
/// **Duas metades com regras opostas.** A metade de cima roda dentro de uma
/// requisicao autenticada e confia no filtro global de tenant. A de baixo roda
/// no worker, que nao tem identidade nenhuma: ali o filtro devolveria vazio, e
/// o backtest "concluiria" sobre zero transacao sem erro nenhum no caminho —
/// exatamente o defeito que a Fase 5 encontrou no despachante da Outbox. Por
/// isso o filtro e desligado pelo nome e a organizacao entra explicitamente na
/// clausula.
/// </summary>
public sealed class RepositorioDeBacktests : IRepositorioDeBacktests
{
    private readonly CentralAntifraudeDbContext _contexto;

    public RepositorioDeBacktests(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    public async Task<Pagina<ExecucaoDeBacktest>> ListarAsync(
        ParametrosDePaginacao parametros,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parametros);

        var consulta = _contexto.ExecucoesDeBacktest
            .AsNoTracking()
            // Da mais recente para a mais antiga: quem abre a tela quer ver o
            // que acabou de pedir. O identificador desempata porque duas
            // execucoes podem ser solicitadas no mesmo instante.
            .OrderByDescending(e => e.SolicitadaEm)
            .ThenByDescending(e => e.Id);

        var total = await consulta.LongCountAsync(cancellationToken);

        var itens = await consulta
            .Skip(parametros.QuantidadeAPular)
            .Take(parametros.Tamanho)
            .ToListAsync(cancellationToken);

        return new Pagina<ExecucaoDeBacktest>(itens, parametros, total);
    }

    /// <summary>Rastreada: quem busca por id pode cancelar em seguida.</summary>
    public Task<ExecucaoDeBacktest?> BuscarAsync(
        Guid execucaoId,
        CancellationToken cancellationToken) =>
        _contexto.ExecucoesDeBacktest
            .FirstOrDefaultAsync(e => e.Id == execucaoId, cancellationToken);

    public Task<int> ContarEmAndamentoAsync(CancellationToken cancellationToken) =>
        _contexto.ExecucoesDeBacktest
            .AsNoTracking()
            .CountAsync(
                e => e.Status == StatusDoBacktest.Pendente
                    || e.Status == StatusDoBacktest.Executando,
                cancellationToken);

    public Task<int> ContarTransacoesNaJanelaAsync(
        DateTimeOffset inicio,
        DateTimeOffset fim,
        CancellationToken cancellationToken) =>
        _contexto.Transacoes
            .AsNoTracking()
            .CountAsync(t => t.OcorridaEm >= inicio && t.OcorridaEm <= fim, cancellationToken);

    public void Adicionar(ExecucaoDeBacktest execucao) =>
        _contexto.ExecucoesDeBacktest.Add(execucao);

    // -----------------------------------------------------------------------
    // Execucao (worker, sem identidade)
    // -----------------------------------------------------------------------

    public Task<ExecucaoDeBacktest?> BuscarParaExecucaoAsync(
        Guid execucaoId,
        CancellationToken cancellationToken) =>
        _contexto.ExecucoesDeBacktest
            .IgnoreQueryFilters([CentralAntifraudeDbContext.FiltroDeTenant])
            .FirstOrDefaultAsync(e => e.Id == execucaoId, cancellationToken);

    /// <summary>
    /// A versao de perfil congelada como comparacao, com as versoes de regra.
    ///
    /// <c>AsNoTracking</c> nao e detalhe de desempenho aqui: e a garantia de
    /// que nada que o backtest le pode ser gravado de volta. A Fase 7 provou
    /// o inverso — entidades com chave preenchida dentro do change tracker
    /// viram <c>UPDATE</c> em tabela declarada imutavel.
    /// </summary>
    public Task<VersaoDePerfilDeRisco?> BuscarVersaoDePerfilAsync(
        Guid organizacaoId,
        Guid versaoDePerfilId,
        CancellationToken cancellationToken) =>
        _contexto.VersoesDePerfilDeRisco
            .IgnoreQueryFilters([CentralAntifraudeDbContext.FiltroDeTenant])
            .AsNoTracking()
            .Include(v => v.VersoesDeRegra)
            .FirstOrDefaultAsync(
                v => v.Id == versaoDePerfilId && v.OrganizacaoId == organizacaoId,
                cancellationToken);

    public async Task<DadosDoBacktest> CarregarDadosAsync(
        ExecucaoDeBacktest execucao,
        TimeSpan janelaDeHistorico,
        int maximoDeContexto,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execucao);

        var organizacao = execucao.OrganizacaoId;

        // O contexto comeca ANTES da janela analisada: cada transacao precisa
        // enxergar o mesmo passado que enxergou na avaliacao real. Sem esse
        // recuo, as transacoes do inicio do periodo seriam avaliadas como se
        // fossem as primeiras do cliente, e as regras de historico ficariam
        // caladas justamente onde deveriam falar.
        var inicioDoContexto = execucao.Inicio - janelaDeHistorico;

        var contexto = await Transacoes(organizacao)
            .Where(t => t.OcorridaEm >= inicioDoContexto && t.OcorridaEm <= execucao.Fim)
            .OrderBy(t => t.OcorridaEm)
            .ThenBy(t => t.Id)
            // Um a mais do que o teto: e assim que se sabe que estourou sem
            // pagar uma consulta de contagem separada.
            .Take(maximoDeContexto + 1)
            .Select(t => new LinhaDoHistorico(
                t.Id,
                t.ClienteExternoId,
                new TransacaoDoHistorico(
                    t.OcorridaEm,
                    t.Valor.Valor,
                    t.Valor.Moeda,
                    t.FingerprintDoDispositivo,
                    t.PaisDeOrigem)))
            .ToListAsync(cancellationToken);

        if (contexto.Count > maximoDeContexto)
        {
            throw new BacktestExcedeuOContexto(maximoDeContexto);
        }

        var analisadas = await Transacoes(organizacao)
            .Where(t => t.OcorridaEm >= execucao.Inicio && t.OcorridaEm <= execucao.Fim)
            .OrderBy(t => t.OcorridaEm)
            .ThenBy(t => t.Id)
            // O conjunto analisado e subconjunto do contexto, entao o mesmo
            // teto o limita com folga. Quem recusa pelo limite menor — o de
            // transacoes analisadas — e o executor.
            .Take(maximoDeContexto + 1)
            .ToListAsync(cancellationToken);

        // Juncao em vez de `Contains` sobre milhares de identificadores: um
        // `IN` com cinco mil parametros e um plano ruim e um comando enorme.
        var vereditos = await (
                from resultado in _contexto.ResultadosDeInvestigacao
                    .IgnoreQueryFilters([CentralAntifraudeDbContext.FiltroDeTenant])
                    .AsNoTracking()
                    .Where(r => r.OrganizacaoId == organizacao)
                join transacao in Transacoes(organizacao)
                    on resultado.TransacaoId equals transacao.Id
                where transacao.OcorridaEm >= execucao.Inicio && transacao.OcorridaEm <= execucao.Fim
                select new { resultado.TransacaoId, resultado.Resultado })
            .ToListAsync(cancellationToken);

        return new DadosDoBacktest(
            analisadas,
            contexto,
            vereditos.ToDictionary(
                v => v.TransacaoId,
                v => v.Resultado));
    }

    /// <summary>
    /// Transacoes de uma organizacao, sem o filtro global e sem rastreamento.
    ///
    /// A organizacao vem por parametro porque o worker nao tem identidade. E
    /// `AsNoTracking` porque um backtest nao altera transacao nenhuma
    /// (ROADMAP 9.5) — sem rastreamento, nao ha nem o caminho para isso.
    /// </summary>
    private IQueryable<Domain.Transacoes.Transacao> Transacoes(Guid organizacaoId) =>
        _contexto.Transacoes
            .IgnoreQueryFilters([CentralAntifraudeDbContext.FiltroDeTenant])
            .AsNoTracking()
            .Where(t => t.OrganizacaoId == organizacaoId);
}
