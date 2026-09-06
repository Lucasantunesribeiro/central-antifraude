using CentralAntifraude.Application.Risco;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Transacoes;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.Infrastructure.Persistencia.Repositorios;

/// <summary>
/// Acesso a perfis, regras e avaliacoes.
///
/// Tudo aqui respeita o filtro de tenant: a avaliacao acontece com a
/// integracao ja autenticada, entao existe contexto — ao contrario da
/// autenticacao, que roda antes.
/// </summary>
public sealed class RepositorioDeRisco : IRepositorioDeRisco
{
    private readonly CentralAntifraudeDbContext _contexto;

    public RepositorioDeRisco(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    /// <summary>
    /// Versao vigente do perfil: a publicada mais recentemente no tenant.
    ///
    /// As versoes de regra vem no mesmo carregamento. Busca-las depois seria
    /// um N+1 dentro do caminho critico da ingestao.
    /// </summary>
    public Task<VersaoDePerfilDeRisco?> BuscarVersaoAtivaDoPerfilAsync(
        CancellationToken cancellationToken) =>
        _contexto.VersoesDePerfilDeRisco
            .Include(v => v.VersoesDeRegra)
            .OrderByDescending(v => v.PublicadaEm)
            .ThenByDescending(v => v.Numero)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<AvaliacaoDeRisco?> BuscarAvaliacaoPorTransacaoAsync(
        Guid transacaoId,
        CancellationToken cancellationToken) =>
        _contexto.AvaliacoesDeRisco
            .FirstOrDefaultAsync(a => a.TransacaoId == transacaoId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, AvaliacaoDeRisco>> BuscarAvaliacoesPorTransacoesAsync(
        IReadOnlyCollection<Guid> transacoesIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transacoesIds);

        if (transacoesIds.Count == 0)
        {
            return new Dictionary<Guid, AvaliacaoDeRisco>();
        }

        // Uma consulta para a pagina inteira, e nao uma por linha: a listagem
        // mostra score e decisao de cada transacao, e um N+1 aqui apareceria
        // na primeira tela com cinquenta itens.
        var avaliacoes = await _contexto.AvaliacoesDeRisco
            .AsNoTracking()
            .Where(a => transacoesIds.Contains(a.TransacaoId))
            .ToListAsync(cancellationToken);

        return avaliacoes.ToDictionary(a => a.TransacaoId);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<SinalDeRisco>>>
        BuscarSinaisPorAvaliacoesAsync(
            IReadOnlyCollection<Guid> avaliacoesIds,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(avaliacoesIds);

        if (avaliacoesIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<SinalDeRisco>>();
        }

        // Uma consulta para a pagina inteira de alertas. O sinal tambem carrega
        // organizacao e tambem tem filtro global, entao partir dele em vez da
        // avaliacao continua isolado por tenant.
        var sinais = await _contexto.Set<SinalDeRisco>()
            .AsNoTracking()
            .Where(s => avaliacoesIds.Contains(s.AvaliacaoId))
            .ToListAsync(cancellationToken);

        return sinais
            .GroupBy(s => s.AvaliacaoId)
            .ToDictionary(
                grupo => grupo.Key,
                // Mesma ordem estavel do resto do produto: maior peso primeiro,
                // depois pelo tipo. Duas leituras do mesmo alerta precisam
                // mostrar os sinais na mesma ordem.
                IReadOnlyList<SinalDeRisco> (grupo) =>
                    [.. grupo
                        .OrderByDescending(s => s.Pontos)
                        .ThenBy(s => s.Tipo)
                        .ThenBy(s => s.RegraId)]);
    }

    public async Task<IReadOnlyList<(Regra Regra, VersaoDeRegra Versao)>> ListarRegrasVigentesAsync(
        CancellationToken cancellationToken)
    {
        var perfil = await BuscarVersaoAtivaDoPerfilAsync(cancellationToken);

        if (perfil is null)
        {
            return [];
        }

        var regrasIds = perfil.VersoesDeRegra.Select(v => v.RegraId).ToList();

        var regras = await _contexto.Regras
            .AsNoTracking()
            .Where(r => regrasIds.Contains(r.Id))
            .ToListAsync(cancellationToken);

        // Ordem estavel por tipo e regra, igual a que o motor usa para
        // executar.
        return perfil.VersoesDeRegra
            .OrderBy(v => v.Tipo)
            .ThenBy(v => v.RegraId)
            .Join(regras, versao => versao.RegraId, regra => regra.Id, (versao, regra) => (regra, versao))
            .ToList();
    }

    public void Adicionar(CatalogoProvisionado catalogo)
    {
        ArgumentNullException.ThrowIfNull(catalogo);

        _contexto.PerfisDeRisco.Add(catalogo.Perfil);
        _contexto.Regras.AddRange(catalogo.Regras);
        _contexto.VersoesDeRegra.AddRange(catalogo.VersoesDeRegra);
        _contexto.VersoesDePerfilDeRisco.Add(catalogo.VersaoDoPerfil);
    }

    public void AdicionarAvaliacao(AvaliacaoDeRisco avaliacao) =>
        _contexto.AvaliacoesDeRisco.Add(avaliacao);

    // -----------------------------------------------------------------------
    // Administracao de regras (Fase 8)
    //
    // Tudo aqui e RASTREADO de proposito, ao contrario das leituras
    // operacionais acima. Duas razoes:
    //
    // 1. a regra e alterada em seguida — rascunho, publicacao, ativacao;
    // 2. as versoes de regra entram na proxima versao de perfil. Traze-las
    //    com `AsNoTracking` faria o EF encontrar entidades com chave
    //    preenchida fora do change tracker e emitir `UPDATE` numa tabela que
    //    o produto declara imutavel.
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<Regra>> ListarTodasAsRegrasAsync(
        CancellationToken cancellationToken) =>
        await _contexto.Regras.ToListAsync(cancellationToken);

    public Task<Regra?> BuscarRegraPorIdAsync(Guid regraId, CancellationToken cancellationToken) =>
        _contexto.Regras.FirstOrDefaultAsync(r => r.Id == regraId, cancellationToken);

    public Task<bool> ExisteRegraComNomeAsync(
        string nome,
        Guid exceto,
        CancellationToken cancellationToken) =>
        _contexto.Regras
            .AsNoTracking()
            .AnyAsync(r => r.Nome == nome && r.Id != exceto, cancellationToken);

    public async Task<IReadOnlyList<VersaoDeRegra>> ListarVersoesDaRegraAsync(
        Guid regraId,
        CancellationToken cancellationToken) =>
        await _contexto.VersoesDeRegra
            .Where(v => v.RegraId == regraId)
            // Da mais recente para a mais antiga: quem abre o historico quer
            // ver primeiro o que esta valendo.
            .OrderByDescending(v => v.Numero)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, VersaoDeRegra>> BuscarUltimasVersoesAsync(
        IReadOnlyCollection<Guid> regrasIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(regrasIds);

        if (regrasIds.Count == 0)
        {
            return new Dictionary<Guid, VersaoDeRegra>();
        }

        // Subconsulta correlacionada em vez de carregar o historico inteiro:
        // uma regra reconfigurada dezenas de vezes nao pode fazer a
        // publicacao de um perfil crescer com o passado.
        var versoes = await _contexto.VersoesDeRegra
            .Where(v => regrasIds.Contains(v.RegraId))
            .Where(v => v.Numero == _contexto.VersoesDeRegra
                .Where(outra => outra.RegraId == v.RegraId)
                .Max(outra => outra.Numero))
            .ToListAsync(cancellationToken);

        return versoes.ToDictionary(v => v.RegraId);
    }

    public Task<PerfilDeRisco?> BuscarPerfilAsync(CancellationToken cancellationToken) =>
        _contexto.PerfisDeRisco
            .OrderBy(p => p.CriadoEm)
            .FirstOrDefaultAsync(cancellationToken);

    public void AdicionarRegra(Regra regra) => _contexto.Regras.Add(regra);

    public void AdicionarVersaoDeRegra(VersaoDeRegra versao) => _contexto.VersoesDeRegra.Add(versao);

    public void AdicionarVersaoDePerfil(VersaoDePerfilDeRisco versao) =>
        _contexto.VersoesDePerfilDeRisco.Add(versao);
}

/// <summary>
/// Carrega o historico do cliente a partir do PostgreSQL.
///
/// Uma consulta por avaliacao, limitada em janela e em quantidade
/// (<see cref="OpcoesDeAvaliacao"/>). Sem os dois limites, um cliente com
/// anos de historico faria o caminho critico da ingestao crescer sem teto.
/// </summary>
public sealed class ProvedorDeContextoDeRisco : IProvedorDeContextoDeRisco
{
    private readonly CentralAntifraudeDbContext _contexto;
    private readonly OpcoesDeAvaliacao _opcoes;

    public ProvedorDeContextoDeRisco(CentralAntifraudeDbContext contexto, OpcoesDeAvaliacao opcoes)
    {
        _contexto = contexto;
        _opcoes = opcoes;
    }

    public async Task<ContextoDeRisco> CarregarAsync(
        Transacao transacao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transacao);

        var inicioDaJanela = transacao.OcorridaEm - _opcoes.JanelaDeHistorico;

        var historico = await _contexto.Transacoes
            .AsNoTracking()
            // O filtro de tenant ja restringe a organizacao; o cliente e a
            // janela vem aqui. O indice
            // (organizacao, cliente_externo_id, ocorrida_em) atende
            // exatamente esta forma de consulta.
            .Where(t => t.ClienteExternoId == transacao.ClienteExternoId)
            .Where(t => t.OcorridaEm >= inicioDaJanela && t.OcorridaEm <= transacao.OcorridaEm)
            // A propria transacao fica de fora: ela e o que esta sendo
            // avaliado. Inclui-la faria a regra de valor comparar a transacao
            // com ela mesma e diluir a media.
            .Where(t => t.Id != transacao.Id)
            .OrderByDescending(t => t.OcorridaEm)
            .ThenByDescending(t => t.Id)
            .Take(_opcoes.MaximoDeTransacoesNoHistorico)
            .Select(t => new TransacaoDoHistorico(
                t.OcorridaEm,
                t.Valor.Valor,
                t.Valor.Moeda,
                t.FingerprintDoDispositivo,
                t.PaisDeOrigem))
            .ToListAsync(cancellationToken);

        return new ContextoDeRisco(historico);
    }
}
