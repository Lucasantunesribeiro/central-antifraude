using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Operacao;
using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.Infrastructure.Persistencia.Repositorios;

/// <summary>
/// As consultas agregadas do painel, das metricas de regra e do detalhe da
/// transacao.
///
/// **Nenhuma desliga o filtro global de tenant.** Todas rodam dentro de uma
/// requisicao autenticada, e um painel que somasse organizacoes seria o
/// vazamento mais silencioso que este produto poderia ter: os numeros sairiam
/// plausiveis e nada denunciaria. Onde um `IgnoreQueryFilters` seria
/// tecnicamente possivel — como na juncao com sinais — ele nao aparece, de
/// proposito.
///
/// **Contagem no banco, e nao em memoria.** Todo agregado aqui vira
/// `GROUP BY` ou `count(*)`: trazer as linhas para somar no processo faria o
/// painel crescer com o volume do cliente, e o painel e a tela que mais abre.
/// </summary>
public sealed class RepositorioDeOperacao : IRepositorioDeOperacao
{
    private readonly CentralAntifraudeDbContext _contexto;

    public RepositorioDeOperacao(CentralAntifraudeDbContext contexto) => _contexto = contexto;

    public Task<int> ContarTransacoesRecebidasAsync(
        JanelaDoPainel janela,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(janela);

        // Por RecebidaEm: "quantas chegaram". E diferente de "quantas foram
        // decididas", que anda por AvaliadaEm — uma transacao atrasada chega
        // hoje sobre um fato de ontem (CLAUDE.md secao 15).
        return _contexto.Transacoes
            .AsNoTracking()
            .CountAsync(t => t.RecebidaEm >= janela.De && t.RecebidaEm <= janela.Ate, cancellationToken);
    }

    public async Task<IReadOnlyList<ContagemPorDecisao>> ContarDecisoesAsync(
        JanelaDoPainel janela,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(janela);

        var contagens = await AvaliacoesDaJanela(janela)
            .GroupBy(a => a.Decisao)
            .Select(grupo => new { Decisao = grupo.Key, Quantidade = grupo.Count() })
            .ToListAsync(cancellationToken);

        var porDecisao = contagens.ToDictionary(c => c.Decisao, c => c.Quantidade);

        // As tres decisoes sempre aparecem, mesmo zeradas. Uma faixa que some
        // da tela quando nao houve nenhuma faria o analista achar que ela
        // deixou de existir.
        return
        [
            .. Enum.GetValues<Decisao>()
                .Order()
                .Select(decisao => new ContagemPorDecisao(
                    decisao,
                    porDecisao.TryGetValue(decisao, out var quantidade) ? quantidade : 0)),
        ];
    }

    public async Task<IReadOnlyList<DiaDoPainel>> ContarDecisoesPorDiaAsync(
        JanelaDoPainel janela,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(janela);

        var contagens = await AvaliacoesDaJanela(janela)
            .GroupBy(a => new { Dia = DateOnly.FromDateTime(a.AvaliadaEm.UtcDateTime), a.Decisao })
            .Select(grupo => new
            {
                grupo.Key.Dia,
                grupo.Key.Decisao,
                Quantidade = grupo.Count(),
            })
            .ToListAsync(cancellationToken);

        var porDia = contagens
            .GroupBy(c => c.Dia)
            .ToDictionary(
                grupo => grupo.Key,
                grupo => grupo.ToDictionary(c => c.Decisao, c => c.Quantidade));

        // A serie sai sem buracos: um dia sem transacao nenhuma vale zero, e
        // nao some. Uma linha do tempo que pula dias faz um fim de semana
        // parado parecer um pico na segunda.
        var primeiro = DateOnly.FromDateTime(janela.De.UtcDateTime);
        var ultimo = DateOnly.FromDateTime(janela.Ate.UtcDateTime);

        var dias = new List<DiaDoPainel>();

        for (var dia = primeiro; dia <= ultimo; dia = dia.AddDays(1))
        {
            porDia.TryGetValue(dia, out var doDia);

            dias.Add(new DiaDoPainel(
                dia,
                Quantidade(doDia, Decisao.Permitir),
                Quantidade(doDia, Decisao.Revisar),
                Quantidade(doDia, Decisao.Bloquear)));
        }

        return dias;
    }

    public async Task<IReadOnlyList<SinalFrequente>> ContarSinaisAsync(
        JanelaDoPainel janela,
        int limite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(janela);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limite);

        // O sinal carrega organizacao e tem filtro global proprio, entao
        // partir dele em vez da avaliacao continua isolado por tenant. A
        // juncao existe para respeitar a janela, que e da avaliacao.
        var contagens = await (
                from sinal in _contexto.Set<SinalDeRisco>().AsNoTracking()
                join avaliacao in AvaliacoesDaJanela(janela)
                    on sinal.AvaliacaoId equals avaliacao.Id
                group sinal by sinal.Tipo into grupo
                select new { Tipo = grupo.Key, Acionamentos = grupo.Count() })
            .ToListAsync(cancellationToken);

        return
        [
            .. contagens
                .OrderByDescending(c => c.Acionamentos)
                // Desempate estavel pelo tipo: dois sinais com a mesma
                // contagem nao podem trocar de lugar entre dois
                // carregamentos da mesma tela.
                .ThenBy(c => c.Tipo)
                .Take(limite)
                .Select(c => new SinalFrequente(c.Tipo, c.Acionamentos)),
        ];
    }

    public Task<int> ContarAlertasAbertosAsync(CancellationToken cancellationToken) =>
        _contexto.Alertas
            .AsNoTracking()
            .CountAsync(a => a.Status == StatusDoAlerta.Aberto, cancellationToken);

    public async Task<IReadOnlyList<ContagemPorStatusDeCaso>> ContarCasosAsync(
        CancellationToken cancellationToken)
    {
        var contagens = await _contexto.Casos
            .AsNoTracking()
            .GroupBy(c => c.Status)
            .Select(grupo => new { Status = grupo.Key, Quantidade = grupo.Count() })
            .ToListAsync(cancellationToken);

        var porStatus = contagens.ToDictionary(c => c.Status, c => c.Quantidade);

        return
        [
            .. Enum.GetValues<StatusDoCaso>()
                .Order()
                .Select(status => new ContagemPorStatusDeCaso(
                    status,
                    porStatus.TryGetValue(status, out var quantidade) ? quantidade : 0)),
        ];
    }

    public Task<int> ContarCasosAntigosAsync(
        DateTimeOffset limite,
        CancellationToken cancellationToken) =>
        _contexto.Casos
            .AsNoTracking()
            .CountAsync(
                c => c.Status != StatusDoCaso.Resolvido && c.AbertoEm < limite,
                cancellationToken);

    public Task<int> ContarEventosPendentesAsync(CancellationToken cancellationToken) =>
        _contexto.EventosDeSaida
            .AsNoTracking()
            .CountAsync(e => e.PublicadoEm == null, cancellationToken);

    public async Task<IReadOnlyList<MetricaDeRegra>> ApurarMetricasDeRegraAsync(
        JanelaDoPainel janela,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(janela);

        // Cada acionamento e um SINAL, e nao uma transacao: uma regra que
        // dispara em cinco transacoes acionou cinco vezes. O veredito, porem,
        // e por transacao — a mesma transacao pode ter acionado varias regras,
        // e o resultado humano dela conta para todas.
        var linhas = await (
                from sinal in _contexto.Set<SinalDeRisco>().AsNoTracking()
                join avaliacao in AvaliacoesDaJanela(janela)
                    on sinal.AvaliacaoId equals avaliacao.Id
                join veredito in _contexto.ResultadosDeInvestigacao.AsNoTracking()
                    on avaliacao.TransacaoId equals veredito.TransacaoId into vereditos
                from veredito in vereditos.DefaultIfEmpty()
                group veredito by new { sinal.RegraId, sinal.Tipo } into grupo
                select new
                {
                    grupo.Key.RegraId,
                    grupo.Key.Tipo,
                    Acionamentos = grupo.Count(),
                    FraudeConfirmada = grupo.Count(
                        v => v != null && v.Resultado == ResultadoDaInvestigacao.FraudeConfirmada),
                    Legitima = grupo.Count(
                        v => v != null && v.Resultado == ResultadoDaInvestigacao.Legitima),
                    Inconclusiva = grupo.Count(
                        v => v != null && v.Resultado == ResultadoDaInvestigacao.Inconclusiva),
                    SemResultado = grupo.Count(v => v == null),
                })
            .ToListAsync(cancellationToken);

        // O nome vem da regra ATUAL, e nao da versao que produziu o sinal:
        // renomear uma regra nao reescreve o passado, mas quem le a metrica
        // hoje precisa reconhece-la pelo nome de hoje (ADR 0013, decisao 6).
        var nomes = await _contexto.Regras
            .AsNoTracking()
            .Select(r => new { r.Id, r.Nome })
            .ToDictionaryAsync(r => r.Id, r => r.Nome, cancellationToken);

        return
        [
            .. linhas
                .OrderByDescending(l => l.Acionamentos)
                .ThenBy(l => l.Tipo)
                .ThenBy(l => l.RegraId)
                .Select(l => new MetricaDeRegra(
                    l.RegraId,
                    nomes.TryGetValue(l.RegraId, out var nome) ? nome : "(regra removida)",
                    l.Tipo,
                    l.Acionamentos,
                    l.FraudeConfirmada,
                    l.Legitima,
                    l.Inconclusiva,
                    l.SemResultado)),
        ];
    }

    public async Task<ContextoOperacionalDaTransacao> CarregarContextoDaTransacaoAsync(
        Guid transacaoId,
        CancellationToken cancellationToken)
    {
        var alertas = await (
                from alerta in _contexto.Alertas.AsNoTracking()
                where alerta.TransacaoId == transacaoId
                join caso in _contexto.Casos.AsNoTracking()
                    on alerta.CasoId equals caso.Id into casos
                from caso in casos.DefaultIfEmpty()
                orderby alerta.CriadoEm
                select new AlertaDaTransacao(
                    alerta.Id,
                    alerta.Prioridade,
                    alerta.Status,
                    alerta.CriadoEm,
                    caso != null ? caso.Id : null,
                    caso != null ? caso.Titulo : null,
                    caso != null ? caso.Status : null))
            .ToListAsync(cancellationToken);

        var veredito = await _contexto.ResultadosDeInvestigacao
            .AsNoTracking()
            .Where(r => r.TransacaoId == transacaoId)
            .Select(r => new
            {
                r.Resultado,
                r.CasoId,
                r.RegistradoEm,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new ContextoOperacionalDaTransacao(
            alertas,
            veredito?.Resultado,
            veredito?.CasoId,
            veredito?.RegistradoEm);
    }

    /// <summary>
    /// Avaliacoes da janela, ancoradas em <c>AvaliadaEm</c>.
    ///
    /// Um so lugar define a ancora: se ela mudasse em um agregado e nao no
    /// outro, o total do painel deixaria de bater com a soma da serie diaria e
    /// ninguem saberia qual dos dois acreditar.
    /// </summary>
    private IQueryable<AvaliacaoDeRisco> AvaliacoesDaJanela(JanelaDoPainel janela) =>
        _contexto.AvaliacoesDeRisco
            .AsNoTracking()
            .Where(a => a.AvaliadaEm >= janela.De && a.AvaliadaEm <= janela.Ate);

    private static int Quantidade(Dictionary<Decisao, int>? contagens, Decisao decisao) =>
        contagens is not null && contagens.TryGetValue(decisao, out var quantidade) ? quantidade : 0;
}

/// <summary>
/// Leitura da trilha de auditoria.
///
/// Separada do registrador de proposito: aquele so escreve. Uma interface
/// unica com leitura e escrita convidaria, no futuro, a um metodo que altera —
/// e uma trilha alteravel nao prova nada (CLAUDE.md secao 67).
/// </summary>
public sealed class ConsultaDeAuditoriaEmPostgres : Application.Auditoria.IConsultaDeAuditoria
{
    private readonly CentralAntifraudeDbContext _contexto;

    public ConsultaDeAuditoriaEmPostgres(CentralAntifraudeDbContext contexto) =>
        _contexto = contexto;

    public async Task<Pagina<Domain.Auditoria.RegistroDeAuditoria>> ListarAsync(
        Application.Auditoria.FiltroDeAuditoria filtro,
        ParametrosDePaginacao paginacao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        ArgumentNullException.ThrowIfNull(paginacao);

        var consulta = _contexto.RegistrosDeAuditoria.AsNoTracking();

        if (filtro.Operacao is { } operacao)
        {
            consulta = consulta.Where(r => r.Operacao == operacao);
        }

        if (filtro.AutorId is { } autor)
        {
            consulta = consulta.Where(r => r.AutorId == autor);
        }

        if (filtro.EntidadeId is { } entidade)
        {
            // O identificador da entidade e guardado como texto porque nem
            // toda entidade auditada tem chave GUID. A comparacao usa o
            // formato canonico, que e o mesmo que a gravacao produz.
            var comoTexto = entidade.ToString();
            consulta = consulta.Where(r => r.EntidadeId == comoTexto);
        }

        if (filtro.De is { } de)
        {
            consulta = consulta.Where(r => r.OcorridoEm >= de);
        }

        if (filtro.Ate is { } ate)
        {
            consulta = consulta.Where(r => r.OcorridoEm <= ate);
        }

        var total = await consulta.LongCountAsync(cancellationToken);

        // Nao ha ordenacao configuravel: a trilha e cronologica, e ordenar por
        // qualquer outra coisa desmontaria a unica leitura que ela suporta —
        // a sequencia dos fatos. O identificador desempata porque dois
        // registros podem cair no mesmo instante.
        var itens = await consulta
            .OrderByDescending(r => r.OcorridoEm)
            .ThenByDescending(r => r.Id)
            .Skip(paginacao.QuantidadeAPular)
            .Take(paginacao.Tamanho)
            .ToListAsync(cancellationToken);

        return new Pagina<Domain.Auditoria.RegistroDeAuditoria>(itens, paginacao, total);
    }

    public async Task<IReadOnlyList<Domain.Auditoria.OperacaoAuditada>> ListarOperacoesUsadasAsync(
        CancellationToken cancellationToken)
    {
        // As que existem na trilha DESTE tenant, e nao o enum inteiro: um
        // filtro que oferece trinta operacoes das quais quatro tem registro
        // faz a pessoa procurar no vazio.
        var usadas = await _contexto.RegistrosDeAuditoria
            .AsNoTracking()
            .Select(r => r.Operacao)
            .Distinct()
            .ToListAsync(cancellationToken);

        return [.. usadas.Order()];
    }
}
