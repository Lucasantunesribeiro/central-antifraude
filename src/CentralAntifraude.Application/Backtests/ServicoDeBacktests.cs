using System.Globalization;
using CentralAntifraude.Application.Auditoria;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Correlacao;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Eventos;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Application.Risco;
using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Backtests;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Tempo;

namespace CentralAntifraude.Application.Backtests;

/// <summary>
/// A porta humana dos backtests: solicitar, acompanhar e cancelar.
///
/// **Nada e executado aqui** (ROADMAP 9.3). A requisicao HTTP monta o
/// candidato, congela, grava a execucao e o evento de saida na mesma
/// transacao, e responde. Rodar milhares de avaliacoes dentro do
/// <c>POST</c> transformaria uma pergunta de supervisao em um prazo de
/// requisicao — e um cliente que desistisse no meio deixaria o trabalho pela
/// metade sem ninguem saber.
///
/// **O candidato e congelado agora, e nao na hora de executar.** O rascunho de
/// uma regra muda a qualquer momento; ler na execucao faria o resultado
/// descrever uma configuracao diferente da que foi pedida.
/// </summary>
public sealed class ServicoDeBacktests
{
    private readonly IRepositorioDeBacktests _backtests;
    private readonly IRepositorioDeRisco _risco;
    private readonly IRepositorioDeUsuarios _usuarios;
    private readonly IRepositorioDeEventos _eventos;
    private readonly IRegistradorDeAuditoria _auditoria;
    private readonly IContextoDoUsuarioAtual _contextoAtual;
    private readonly IContextoDeCorrelacao _correlacao;
    private readonly IUnidadeDeTrabalho _unidadeDeTrabalho;
    private readonly IDespachanteImediato _despachanteImediato;
    private readonly OpcoesDeBacktest _opcoes;
    private readonly IRelogio _relogio;

    public ServicoDeBacktests(
        IRepositorioDeBacktests backtests,
        IRepositorioDeRisco risco,
        IRepositorioDeUsuarios usuarios,
        IRepositorioDeEventos eventos,
        IRegistradorDeAuditoria auditoria,
        IContextoDoUsuarioAtual contextoAtual,
        IContextoDeCorrelacao correlacao,
        IUnidadeDeTrabalho unidadeDeTrabalho,
        IDespachanteImediato despachanteImediato,
        OpcoesDeBacktest opcoes,
        IRelogio relogio)
    {
        _backtests = backtests;
        _risco = risco;
        _usuarios = usuarios;
        _eventos = eventos;
        _auditoria = auditoria;
        _contextoAtual = contextoAtual;
        _correlacao = correlacao;
        _unidadeDeTrabalho = unidadeDeTrabalho;
        _despachanteImediato = despachanteImediato;
        _opcoes = opcoes;
        _relogio = relogio;
    }

    public async Task<Pagina<ExecucaoDeBacktest>> ListarAsync(
        ParametrosDePaginacao paginacao,
        CancellationToken cancellationToken)
    {
        GarantirSupervisao();

        return await _backtests.ListarAsync(paginacao, cancellationToken);
    }

    public async Task<ExecucaoDeBacktest> ObterAsync(
        Guid execucaoId,
        CancellationToken cancellationToken)
    {
        GarantirSupervisao();

        return await _backtests.BuscarAsync(execucaoId, cancellationToken)
            ?? throw new RecursoNaoEncontrado("Backtest");
    }

    /// <summary>
    /// Monta o perfil candidato, congela e enfileira.
    ///
    /// <paramref name="regraId"/> aponta a regra cujo rascunho e a mudanca.
    /// Nulo significa "so os limiares mudam" — que tambem e uma pergunta
    /// legitima, e a unica que o Supervisor tinha antes desta fase.
    /// </summary>
    public async Task<ExecucaoDeBacktest> SolicitarAsync(
        Guid? regraId,
        int? limiarDeRevisao,
        int? limiarDeBloqueio,
        DateTimeOffset inicio,
        DateTimeOffset fim,
        CancellationToken cancellationToken)
    {
        GarantirSupervisao();
        GarantirJanela(inicio, fim);

        var vigente = await _risco.BuscarVersaoAtivaDoPerfilAsync(cancellationToken)
            ?? throw new RecursoNaoEncontrado("Perfil de risco");

        var (candidato, descricao) = await MontarCandidatoAsync(
            regraId,
            limiarDeRevisao ?? vigente.LimiarDeRevisao,
            limiarDeBloqueio ?? vigente.LimiarDeBloqueio,
            vigente,
            cancellationToken);

        await GarantirFolgaDeExecucoesAsync(cancellationToken);
        await GarantirVolumeAsync(inicio, fim, cancellationToken);

        var autor = await AutorAtualAsync(cancellationToken);

        var execucao = Traduzindo(() => ExecucaoDeBacktest.Solicitar(
            _contextoAtual.OrganizacaoId,
            descricao,
            regraId,
            candidato,
            vigente.Id,
            vigente.Numero,
            inicio,
            fim,
            autor.Id,
            autor.NomeCompleto,
            _relogio.Agora));

        _backtests.Adicionar(execucao);

        // Execucao e evento na MESMA transacao (CLAUDE.md secao 38). Enviar
        // direto para a fila aqui abriria o dual-write que a Outbox existe
        // para fechar: a mensagem sairia mesmo que o commit falhasse, e um
        // worker procuraria uma execucao que nunca existiu.
        _eventos.Adicionar(EventoDeSaida.Registrar(
            execucao.OrganizacaoId,
            new BacktestSolicitadoV1(execucao.Id),
            execucao.SolicitadaEm,
            _correlacao.IdDeCorrelacao));

        await AuditarAsync(
            OperacaoAuditada.BacktestSolicitado,
            execucao,
            $"{descricao}; janela {Curto(inicio)} a {Curto(fim)}",
            cancellationToken);

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        // DEPOIS do commit, como na ingestao.
        //
        // **Esta linha faltava, e o defeito so apareceu em producao.** O evento
        // `BacktestSolicitado.v1` ficava na Outbox com zero tentativas ate a
        // varredura de quinze minutos passar. Nao havia erro em lugar nenhum:
        // o backtest ficava "Pendente", e quem pediu concluia que travou.
        //
        // A licao e que o despacho imediato e uma responsabilidade de TODO
        // produtor de evento, e nao um detalhe do caminho de ingestao. Enquanto
        // ele for chamada explicita, cada produtor novo precisa lembrar — e o
        // teste `SolicitarBacktestAcordaODespachante` existe para que o
        // esquecimento apareca no CI, e nao no ambiente publicado.
        await _despachanteImediato.AcordarAsync(cancellationToken);

        return execucao;
    }

    /// <summary>
    /// Cancela uma execucao pendente ou em andamento.
    ///
    /// **Nao ha sinal enviado ao worker.** Cancelar sobe o token de versao da
    /// linha; o worker que estiver executando tenta concluir com a versao
    /// antiga e e recusado pelo banco. Isso torna o cancelamento seguro sem
    /// nenhuma consulta de verificacao dentro do laco — e sem a janela em que
    /// um worker "quase la" grava o resultado depois do cancelamento.
    /// </summary>
    public async Task<ExecucaoDeBacktest> CancelarAsync(
        Guid execucaoId,
        int versaoEsperada,
        CancellationToken cancellationToken)
    {
        GarantirSupervisao();

        var execucao = await _backtests.BuscarAsync(execucaoId, cancellationToken)
            ?? throw new RecursoNaoEncontrado("Backtest");

        if (execucao.Versao != versaoEsperada)
        {
            throw new ConflitoDeEstado(
                "versao_desatualizada",
                "A execucao mudou depois que voce a abriu. Recarregue para ver em que ponto ela esta.");
        }

        Traduzindo(() => execucao.Cancelar(_relogio.Agora));

        await AuditarAsync(
            OperacaoAuditada.BacktestCancelado,
            execucao,
            execucao.Descricao,
            cancellationToken);

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return execucao;
    }

    // -----------------------------------------------------------------------
    // Montagem do candidato
    // -----------------------------------------------------------------------

    /// <summary>
    /// Compoe o perfil que seria publicado.
    ///
    /// A composicao e a MESMA de <c>ServicoDeGestaoDeRegras.SucederPerfilAsync</c>
    /// — ultima versao publicada de cada regra ativa, na ordem tipo/regra —
    /// com uma unica troca: a regra candidata entra com o rascunho dela. Se a
    /// composicao divergisse, o backtest mediria um perfil que a publicacao
    /// nunca produziria.
    /// </summary>
    private async Task<(PerfilCandidato Candidato, string Descricao)> MontarCandidatoAsync(
        Guid? regraId,
        int limiarDeRevisao,
        int limiarDeBloqueio,
        VersaoDePerfilDeRisco vigente,
        CancellationToken cancellationToken)
    {
        var regras = await _risco.ListarTodasAsRegrasAsync(cancellationToken);
        var ativas = regras.Where(r => r.Ativa).ToList();

        Regra? candidata = null;

        if (regraId is { } id)
        {
            candidata = regras.FirstOrDefault(r => r.Id == id)
                ?? throw new RecursoNaoEncontrado("Regra");

            if (!candidata.Ativa)
            {
                throw new ConflitoDeEstado(
                    "regra_desativada",
                    "Uma regra desativada nao entra no perfil. Reative antes de simular.");
            }

            if (!candidata.TemRascunho)
            {
                // Sem rascunho nao ha o que simular: o candidato seria
                // identico ao vigente, e um resultado de "nada muda" custaria
                // uma execucao inteira para dizer o obvio.
                throw new ConflitoDeEstado(
                    "sem_rascunho",
                    "Esta regra nao tem alteracoes pendentes. Edite o rascunho antes de simular.");
            }
        }

        var ultimas = await _risco.BuscarUltimasVersoesAsync(
            [.. ativas.Select(r => r.Id)],
            cancellationToken);

        var candidatas = new List<RegraCandidata>();

        foreach (var regra in ativas.OrderBy(r => r.Tipo).ThenBy(r => r.Id))
        {
            if (candidata is not null && regra.Id == candidata.Id)
            {
                candidatas.Add(new RegraCandidata(
                    regra.Id,
                    regra.Nome,
                    regra.Tipo,
                    regra.ConfiguracaoEmRascunho!,
                    regra.PontosEmRascunho!.Value,
                    OrigemDaRegraCandidata.Rascunho));
            }
            else if (ultimas.TryGetValue(regra.Id, out var versao))
            {
                candidatas.Add(new RegraCandidata(
                    regra.Id,
                    regra.Nome,
                    regra.Tipo,
                    versao.Configuracao,
                    versao.Pontos,
                    OrigemDaRegraCandidata.Publicada));
            }
        }

        var descricao = candidata is not null
            ? $"Rascunho de \"{candidata.Nome}\""
            : $"Limiares {vigente.LimiarDeRevisao}/{vigente.LimiarDeBloqueio} -> " +
              $"{limiarDeRevisao}/{limiarDeBloqueio}";

        return (new PerfilCandidato(limiarDeRevisao, limiarDeBloqueio, candidatas), descricao);
    }

    // -----------------------------------------------------------------------
    // Limites
    // -----------------------------------------------------------------------

    private void GarantirJanela(DateTimeOffset inicio, DateTimeOffset fim)
    {
        if (fim <= inicio)
        {
            throw new ErroDeValidacao("fim", "O fim da janela precisa ser depois do inicio.");
        }

        if (fim - inicio > _opcoes.JanelaMaxima)
        {
            throw new ErroDeValidacao(
                "inicio",
                $"A janela nao pode passar de {_opcoes.JanelaMaximaEmDias} dias. " +
                "Simule periodos menores em execucoes separadas.");
        }
    }

    private async Task GarantirFolgaDeExecucoesAsync(CancellationToken cancellationToken)
    {
        var emAndamento = await _backtests.ContarEmAndamentoAsync(cancellationToken);

        if (emAndamento >= _opcoes.MaximoDeExecucoesEmAndamento)
        {
            throw new ConflitoDeEstado(
                "limite_de_execucoes",
                $"Ja ha {emAndamento} execucao(oes) em andamento nesta organizacao, e o limite e " +
                $"{_opcoes.MaximoDeExecucoesEmAndamento}. Espere terminar ou cancele uma delas.");
        }
    }

    private async Task GarantirVolumeAsync(
        DateTimeOffset inicio,
        DateTimeOffset fim,
        CancellationToken cancellationToken)
    {
        var total = await _backtests.ContarTransacoesNaJanelaAsync(inicio, fim, cancellationToken);

        if (total > _opcoes.MaximoDeTransacoesAnalisadas)
        {
            // Recusar, e nao truncar. Um resultado sobre um pedaco arbitrario
            // do periodo pareceria completo e levaria a decisao errada.
            throw new ErroDeValidacao(
                "inicio",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A janela tem {total} transacoes e o limite por execucao e " +
                    $"{_opcoes.MaximoDeTransacoesAnalisadas}. Escolha um periodo menor."));
        }
    }

    // -----------------------------------------------------------------------
    // Apoio
    // -----------------------------------------------------------------------

    private void GarantirSupervisao()
    {
        // Executar backtest e supervisao (CLAUDE.md secao 8.2). A politica da
        // rota ja recusa antes daqui; esta e a segunda camada, para o caso de
        // uma rota futura esquecer a politica.
        if (_contextoAtual.Perfil
            is not (PerfilDeUsuario.Administrador or PerfilDeUsuario.SupervisorDeFraude))
        {
            throw new NaoAutorizado("Executar backtests e uma acao de supervisao.");
        }
    }

    private async Task<Usuario> AutorAtualAsync(CancellationToken cancellationToken) =>
        await _usuarios.BuscarPorIdAsync(_contextoAtual.UsuarioId, cancellationToken)
            ?? throw new NaoAutenticado("sessao_invalida", "Sessao nao corresponde a um usuario ativo.");

    private Task AuditarAsync(
        OperacaoAuditada operacao,
        ExecucaoDeBacktest execucao,
        string detalhe,
        CancellationToken cancellationToken) =>
        _auditoria.RegistrarAsync(
            RegistroDeAuditoria.Registrar(
                execucao.OrganizacaoId,
                operacao,
                nameof(ExecucaoDeBacktest),
                _relogio.Agora,
                autorId: _contextoAtual.UsuarioId,
                entidadeId: execucao.Id.ToString(),
                detalhe: detalhe,
                idDeCorrelacao: _correlacao.IdDeCorrelacao),
            cancellationToken);

    private static string Curto(DateTimeOffset instante) =>
        instante.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Traduz a recusa do dominio para o contrato HTTP.
    ///
    /// Cancelar uma execucao ja concluida nao e defeito do servidor: e o
    /// estado do recurso recusando a operacao.
    /// </summary>
    private static T Traduzindo<T>(Func<T> operacao)
    {
        try
        {
            return operacao();
        }
        catch (ViolacaoDeInvariante excecao)
        {
            throw new ConflitoDeEstado("estado_do_backtest", excecao.Message);
        }
    }

    private static void Traduzindo(Action operacao) =>
        Traduzindo<object?>(() =>
        {
            operacao();
            return null;
        });
}
