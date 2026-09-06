using CentralAntifraude.Application.Alertas;
using CentralAntifraude.Application.Auditoria;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Application.Integracoes;
using CentralAntifraude.Application.Risco;
using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Tempo;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Application.Investigacao;

/// <summary>
/// A operacao de investigacao.
///
/// **Onde cada regra mora.** O dominio decide o que e um caso valido —
/// transicoes, imutabilidade do resolvido, timeline na mesma operacao. Este
/// servico decide **quem pode** fazer cada coisa, porque e aqui que o perfil
/// do usuario e conhecido. Misturar os dois faria o `Caso` depender de
/// autenticacao, e um teste de dominio precisaria montar uma sessao para
/// verificar uma transicao de estado.
///
/// **Toda excecao de invariante do dominio vira conflito HTTP.** Uma tentativa
/// de resolver um caso ja resolvido nao e um defeito do servidor: e o estado
/// do recurso recusando a operacao, e o cliente precisa distinguir isso de uma
/// falha real.
/// </summary>
public sealed class ServicoDeCasos
{
    /// <summary>Campos pelos quais a lista de casos pode ser ordenada.</summary>
    public static readonly string[] CamposDeOrdenacao = ["atualizadoEm", "abertoEm"];

    /// <summary>
    /// Ordem padrao: o que mexeu por ultimo primeiro.
    ///
    /// Nao por abertura. Um caso aberto ha uma semana que recebeu nota agora e
    /// mais relevante para quem esta trabalhando do que um caso aberto hoje e
    /// intocado.
    /// </summary>
    public const string OrdenacaoPadrao = "atualizadoEm";

    private readonly IRepositorioDeCasos _casos;
    private readonly IRepositorioDeAlertas _alertas;
    private readonly IRepositorioDeUsuarios _usuarios;
    private readonly IRepositorioDeTransacoes _transacoes;
    private readonly IRepositorioDeRisco _risco;
    private readonly IContextoDoUsuarioAtual _contextoAtual;
    private readonly IRegistradorDeAuditoria _auditoria;
    private readonly IUnidadeDeTrabalho _unidadeDeTrabalho;
    private readonly IRelogio _relogio;

    public ServicoDeCasos(
        IRepositorioDeCasos casos,
        IRepositorioDeAlertas alertas,
        IRepositorioDeUsuarios usuarios,
        IRepositorioDeTransacoes transacoes,
        IRepositorioDeRisco risco,
        IContextoDoUsuarioAtual contextoAtual,
        IRegistradorDeAuditoria auditoria,
        IUnidadeDeTrabalho unidadeDeTrabalho,
        IRelogio relogio)
    {
        _casos = casos;
        _alertas = alertas;
        _usuarios = usuarios;
        _transacoes = transacoes;
        _risco = risco;
        _contextoAtual = contextoAtual;
        _auditoria = auditoria;
        _unidadeDeTrabalho = unidadeDeTrabalho;
        _relogio = relogio;
    }

    // -----------------------------------------------------------------------
    // Leitura
    // -----------------------------------------------------------------------

    public async Task<Pagina<CasoNaLista>> ListarAsync(
        FiltroDeCasos filtro,
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken)
    {
        var pagina = await _casos.ListarAsync(filtro, paginacao, ordenacao, cancellationToken);

        // Duas consultas em lote para a pagina inteira, e nao duas por linha.
        var resumos = await _casos.ResumirAlertasAsync(
            [.. pagina.Itens.Select(c => c.Id)],
            cancellationToken);

        var nomes = await NomesDeAsync(
            [.. pagina.Itens.Where(c => c.ResponsavelId is not null).Select(c => c.ResponsavelId!.Value)],
            cancellationToken);

        var itens = pagina.Itens
            .Select(caso =>
            {
                var resumo = resumos.TryGetValue(caso.Id, out var encontrado)
                    ? encontrado
                    : new ResumoDosAlertas(0, 0, null);

                return new CasoNaLista(
                    caso,
                    caso.ResponsavelId is { } id && nomes.TryGetValue(id, out var nome) ? nome : null,
                    resumo.Quantidade,
                    resumo.MaiorScore,
                    resumo.MaiorPrioridade);
            })
            .ToList();

        return new Pagina<CasoNaLista>(itens, paginacao, pagina.TotalDeItens);
    }

    /// <summary>
    /// O workspace da investigacao.
    ///
    /// Caso de outro tenant nao existe daqui: o filtro global nao o devolve e a
    /// resposta e 404, sem revelar que o identificador e valido em outro lugar
    /// (`CLAUDE.md` secao 52).
    /// </summary>
    public async Task<CasoCompleto> ObterAsync(Guid casoId, CancellationToken cancellationToken)
    {
        var caso = await _casos.BuscarPorIdAsync(casoId, cancellationToken)
            ?? throw new RecursoNaoEncontrado("Caso");

        var alertas = await _casos.ListarAlertasDoCasoAsync(casoId, cancellationToken);
        var timeline = await _casos.ListarTimelineAsync(casoId, cancellationToken);
        var notas = await _casos.ListarNotasAsync(casoId, cancellationToken);

        var transacoes = await _transacoes.BuscarPorIdsAsync(
            [.. alertas.Select(a => a.TransacaoId)],
            cancellationToken);

        var sinais = await _risco.BuscarSinaisPorAvaliacoesAsync(
            [.. alertas.Select(a => a.AvaliacaoId)],
            cancellationToken);

        var pessoas = await NomesDeAsync(
            [.. new[] { caso.ResponsavelId, caso.AbertoPorId, caso.ResolvidoPorId }
                .Where(id => id is not null)
                .Select(id => id!.Value)],
            cancellationToken);

        var comContexto = alertas
            .Select(alerta => new AlertaDoCaso(
                alerta,
                transacoes.TryGetValue(alerta.TransacaoId, out var transacao) ? transacao : null,
                sinais.TryGetValue(alerta.AvaliacaoId, out var doAlerta) ? doAlerta : []))
            .ToList();

        return new CasoCompleto(
            caso,
            NomeOuNulo(caso.ResponsavelId, pessoas),
            NomeOuNulo(caso.AbertoPorId, pessoas),
            NomeOuNulo(caso.ResolvidoPorId, pessoas),
            comContexto,
            timeline,
            notas,
            AcoesPermitidas(caso));
    }

    // -----------------------------------------------------------------------
    // Escrita
    // -----------------------------------------------------------------------

    /// <summary>Abre um caso a partir de alertas da propria organizacao.</summary>
    public async Task<Caso> AbrirAsync(
        string titulo,
        IReadOnlyCollection<Guid> alertasIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alertasIds);

        GarantirPerfilOperacional();

        if (alertasIds.Count == 0)
        {
            throw new ErroDeValidacao("alertasIds", "Informe ao menos um alerta.");
        }

        // Conteudo invalido e 400, e nao 409: o recurso nao esta num estado
        // incompativel, o texto e que nao serve. A regra continua sendo uma so,
        // no dominio — aqui ela e apenas consultada antes de lancar.
        GarantirTexto(titulo, Caso.TamanhoMinimoDoTitulo, Caso.TamanhoMaximoDoTitulo, "titulo");

        var alertas = await _alertas.BuscarPorIdsAsync(alertasIds, cancellationToken);

        // Um identificador que nao volta pode ser inexistente OU de outro
        // tenant — e os dois casos precisam ser indistinguiveis. Dizer "alerta
        // de outra organizacao" confirmaria que aquele identificador existe.
        if (alertas.Count != alertasIds.Distinct().Count())
        {
            throw new RecursoNaoEncontrado("Alerta");
        }

        var autor = await AutorAtualAsync(cancellationToken);

        var caso = Traduzindo(() => Caso.Abrir(
            _contextoAtual.OrganizacaoId,
            titulo,
            alertas,
            autor.Id,
            autor.NomeCompleto,
            _relogio.Agora));

        _casos.Adicionar(caso);

        await AuditarAsync(OperacaoAuditada.CasoAberto, caso, $"{alertas.Count} alerta(s)", cancellationToken);
        await SalvarAsync(cancellationToken);

        return caso;
    }

    public async Task<Caso> AssociarAlertaAsync(
        Guid casoId,
        Guid alertaId,
        int versaoEsperada,
        CancellationToken cancellationToken)
    {
        GarantirPerfilOperacional();

        var caso = await CarregarParaEscritaAsync(casoId, versaoEsperada, cancellationToken);
        var autor = await AutorAtualAsync(cancellationToken);

        var alerta = await _alertas.BuscarPorIdAsync(alertaId, cancellationToken)
            ?? throw new RecursoNaoEncontrado("Alerta");

        Traduzindo(() => caso.AssociarAlerta(alerta, autor.Id, autor.NomeCompleto, _relogio.Agora));

        _casos.RegistrarNovidades(caso);

        await AuditarAsync(OperacaoAuditada.CasoAlterado, caso, $"Alerta {alertaId} associado", cancellationToken);
        await SalvarAsync(cancellationToken);

        return caso;
    }

    public async Task<Caso> AssumirAsync(
        Guid casoId,
        int versaoEsperada,
        CancellationToken cancellationToken)
    {
        GarantirPerfilOperacional();

        var caso = await CarregarParaEscritaAsync(casoId, versaoEsperada, cancellationToken);
        var autor = await AutorAtualAsync(cancellationToken);

        Traduzindo(() => caso.Assumir(autor.Id, autor.NomeCompleto, _relogio.Agora));

        _casos.RegistrarNovidades(caso);

        await AuditarAsync(OperacaoAuditada.CasoAtribuido, caso, "Assumido pelo proprio autor", cancellationToken);
        await SalvarAsync(cancellationToken);

        return caso;
    }

    /// <summary>
    /// Passa o caso para outra pessoa.
    ///
    /// **So Supervisor e Administrador.** Assumir e pegar um caso livre para
    /// si; transferir e tirar o caso de alguem, e isso e ato de supervisao
    /// (`CLAUDE.md` secao 8.2). Sem essa separacao, qualquer analista poderia
    /// esvaziar a fila de outro sem que ninguem tivesse decidido isso.
    /// </summary>
    public async Task<Caso> TransferirAsync(
        Guid casoId,
        Guid paraUsuarioId,
        int versaoEsperada,
        CancellationToken cancellationToken)
    {
        if (_contextoAtual.Perfil is not (PerfilDeUsuario.Administrador or PerfilDeUsuario.SupervisorDeFraude))
        {
            throw new NaoAutorizado("Transferir um caso e uma acao de supervisao.");
        }

        var caso = await CarregarParaEscritaAsync(casoId, versaoEsperada, cancellationToken);
        var autor = await AutorAtualAsync(cancellationToken);

        // Busca dentro do tenant: um identificador de usuario de outra
        // organizacao simplesmente nao existe daqui.
        var destino = await _usuarios.BuscarPorIdAsync(paraUsuarioId, cancellationToken)
            ?? throw new RecursoNaoEncontrado("Usuario");

        if (!destino.Ativo)
        {
            throw new ConflitoDeEstado(
                "destinatario_inativo",
                "Nao da para transferir um caso para um usuario desativado.");
        }

        if (destino.Perfil == PerfilDeUsuario.Auditor)
        {
            throw new ConflitoDeEstado(
                "destinatario_sem_perfil_operacional",
                "O perfil Auditor e de leitura e nao pode ser responsavel por uma investigacao.");
        }

        Traduzindo(() => caso.Transferir(
            destino.Id,
            destino.NomeCompleto,
            autor.Id,
            autor.NomeCompleto,
            _relogio.Agora));

        _casos.RegistrarNovidades(caso);

        await AuditarAsync(
            OperacaoAuditada.CasoAtribuido,
            caso,
            $"Transferido para {destino.Id}",
            cancellationToken);

        await SalvarAsync(cancellationToken);

        return caso;
    }

    public async Task<NotaDoCaso> AdicionarNotaAsync(
        Guid casoId,
        string conteudo,
        int versaoEsperada,
        CancellationToken cancellationToken)
    {
        GarantirPerfilOperacional();

        GarantirTexto(
            conteudo,
            NotaDoCaso.TamanhoMinimoDoConteudo,
            NotaDoCaso.TamanhoMaximoDoConteudo,
            "conteudo");

        var caso = await CarregarParaEscritaAsync(casoId, versaoEsperada, cancellationToken);
        var autor = await AutorAtualAsync(cancellationToken);

        var nota = Traduzindo(() => caso.AdicionarNota(
            conteudo,
            autor.Id,
            autor.NomeCompleto,
            _relogio.Agora));

        // O CONTEUDO da nota nao entra na trilha de auditoria: ele ja esta
        // gravado na propria nota, que e append-only, e duplica-lo espalharia
        // texto livre do tenant por mais um lugar.
        _casos.RegistrarNovidades(caso);

        await AuditarAsync(OperacaoAuditada.CasoAlterado, caso, "Nota adicionada", cancellationToken);
        await SalvarAsync(cancellationToken);

        return nota;
    }

    /// <summary>
    /// Conclui a investigacao.
    ///
    /// Alem do estado do caso, quem resolve precisa ser o responsavel — ou
    /// alguem de supervisao. Um analista concluindo o caso de outro sem
    /// transferencia deixaria a timeline dizendo que uma pessoa investigou e
    /// outra decidiu, sem nada explicando a troca.
    /// </summary>
    public async Task<Caso> ResolverAsync(
        Guid casoId,
        ResultadoDaInvestigacao resultado,
        int versaoEsperada,
        CancellationToken cancellationToken)
    {
        GarantirPerfilOperacional();

        var caso = await CarregarParaEscritaAsync(casoId, versaoEsperada, cancellationToken);
        var autor = await AutorAtualAsync(cancellationToken);

        var ehSupervisao = _contextoAtual.Perfil
            is PerfilDeUsuario.Administrador or PerfilDeUsuario.SupervisorDeFraude;

        if (caso.ResponsavelId != autor.Id && !ehSupervisao)
        {
            throw new NaoAutorizado(
                "So o responsavel pelo caso, ou a supervisao, pode resolve-lo.");
        }

        var alertas = await _casos.ListarAlertasDoCasoAsync(casoId, cancellationToken);

        var veredictos = Traduzindo(() => caso.Resolver(
            resultado,
            alertas,
            autor.Id,
            autor.NomeCompleto,
            _relogio.Agora));

        _casos.RegistrarNovidades(caso);
        _casos.AdicionarVeredictos(veredictos);

        await AuditarAsync(
            OperacaoAuditada.CasoResolvido,
            caso,
            $"Resultado: {resultado}; {veredictos.Count} transacao(oes)",
            cancellationToken);

        await SalvarAsync(cancellationToken);

        return caso;
    }

    // -----------------------------------------------------------------------
    // Apoio
    // -----------------------------------------------------------------------

    /// <summary>
    /// Grava, traduzindo a corrida perdida em conflito.
    ///
    /// **Duas camadas protegem o caso, e as duas podem falhar primeiro.** O
    /// token de versao recusa a segunda gravacao com zero linhas atingidas; a
    /// restricao unica de <c>resultados_de_investigacao.transacao_id</c> recusa
    /// o segundo veredito sobre a mesma transacao. Qual das duas dispara antes
    /// depende de quem chegou onde primeiro — e o cliente nao deveria receber
    /// respostas diferentes por causa disso.
    ///
    /// Sem esta traducao, a segunda resolucao simultanea virava **500**: o
    /// integrador leria "algo quebrou" quando a resposta certa e "outra pessoa
    /// concluiu antes; recarregue".
    /// </summary>
    private async Task SalvarAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _unidadeDeTrabalho.SalvarAsync(cancellationToken);
        }
        catch (ConflitoDeUnicidadeNoBanco excecao)
        {
            throw new ConflitoDeEstado(
                "conflito_de_concorrencia",
                "Outra pessoa concluiu esta operacao antes de voce. Recarregue o caso para ver o que aconteceu.",
                excecao);
        }
    }

    /// <summary>
    /// Carrega o caso e confere a versao que o cliente leu.
    ///
    /// Esta e a **primeira** camada contra lost update, e ela existe para dar
    /// um conflito claro no caso comum. A segunda camada e o token de
    /// concorrencia no banco, que segura duas requisicoes que passem juntas
    /// por aqui.
    /// </summary>
    private async Task<Caso> CarregarParaEscritaAsync(
        Guid casoId,
        int versaoEsperada,
        CancellationToken cancellationToken)
    {
        var caso = await _casos.BuscarPorIdAsync(casoId, cancellationToken)
            ?? throw new RecursoNaoEncontrado("Caso");

        if (caso.Versao != versaoEsperada)
        {
            throw new ConflitoDeEstado(
                "versao_desatualizada",
                "O caso mudou depois que voce o abriu. Recarregue para ver o que aconteceu antes de agir.");
        }

        return caso;
    }

    private async Task<Usuario> AutorAtualAsync(CancellationToken cancellationToken) =>
        await _usuarios.BuscarPorIdAsync(_contextoAtual.UsuarioId, cancellationToken)
            ?? throw new NaoAutenticado("sessao_invalida", "Sessao nao corresponde a um usuario ativo.");

    /// <summary>
    /// Aplica a mesma regra de texto do dominio, mas devolvendo erro de
    /// validacao. A regra nao e reescrita: e a mesma funcao.
    /// </summary>
    private static void GarantirTexto(string? valor, int minimo, int maximo, string campo)
    {
        if (!TextoDeInvestigacao.EhValido(valor, minimo, maximo, campo, out _, out var erro))
        {
            throw new ErroDeValidacao(campo, erro);
        }
    }

    private void GarantirPerfilOperacional()
    {
        // O Auditor le tudo e nao age em nada (`CLAUDE.md` secao 8.3). A
        // politica de autorizacao da rota ja recusa antes daqui; esta e a
        // segunda camada, para o caso de uma rota futura esquecer a politica.
        if (_contextoAtual.Perfil is null or PerfilDeUsuario.Auditor)
        {
            throw new NaoAutorizado("O perfil Auditor e de leitura e nao age sobre casos.");
        }
    }

    private HashSet<string> AcoesPermitidas(Caso caso)
    {
        var acoes = new HashSet<string>(StringComparer.Ordinal);

        if (caso.EstaResolvido || _contextoAtual.Perfil is null or PerfilDeUsuario.Auditor)
        {
            return acoes;
        }

        var ehSupervisao = _contextoAtual.Perfil
            is PerfilDeUsuario.Administrador or PerfilDeUsuario.SupervisorDeFraude;

        acoes.Add(AcoesDoCaso.AssociarAlerta);
        acoes.Add(AcoesDoCaso.AdicionarNota);

        if (caso.ResponsavelId is null)
        {
            acoes.Add(AcoesDoCaso.Assumir);
        }

        if (ehSupervisao)
        {
            acoes.Add(AcoesDoCaso.Transferir);
        }

        if (caso.Status == StatusDoCaso.EmAnalise &&
            (caso.ResponsavelId == _contextoAtual.UsuarioId || ehSupervisao))
        {
            acoes.Add(AcoesDoCaso.Resolver);
        }

        return acoes;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> NomesDeAsync(
        IReadOnlyCollection<Guid> usuariosIds,
        CancellationToken cancellationToken) =>
        usuariosIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _usuarios.BuscarNomesPorIdsAsync(usuariosIds, cancellationToken);

    private static string? NomeOuNulo(Guid? id, IReadOnlyDictionary<Guid, string> nomes) =>
        id is { } valor && nomes.TryGetValue(valor, out var nome) ? nome : null;

    private Task AuditarAsync(
        OperacaoAuditada operacao,
        Caso caso,
        string detalhe,
        CancellationToken cancellationToken) =>
        _auditoria.RegistrarAsync(
            RegistroDeAuditoria.Registrar(
                caso.OrganizacaoId,
                operacao,
                nameof(Caso),
                _relogio.Agora,
                autorId: _contextoAtual.UsuarioId,
                entidadeId: caso.Id.ToString(),
                detalhe: detalhe),
            cancellationToken);

    /// <summary>
    /// Traduz a recusa do dominio para o contrato HTTP.
    ///
    /// Uma invariante violada aqui nao e defeito do servidor: e o estado do
    /// recurso recusando a operacao. Sem esta traducao, tentar resolver um
    /// caso ja resolvido viraria 500, e o cliente nao teria como distinguir
    /// isso de uma falha de verdade.
    /// </summary>
    private static T Traduzindo<T>(Func<T> operacao)
    {
        try
        {
            return operacao();
        }
        catch (ViolacaoDeInvariante excecao)
        {
            throw new ConflitoDeEstado("estado_do_caso", excecao.Message);
        }
    }

    private static void Traduzindo(Action operacao) =>
        Traduzindo<object?>(() =>
        {
            operacao();
            return null;
        });
}
