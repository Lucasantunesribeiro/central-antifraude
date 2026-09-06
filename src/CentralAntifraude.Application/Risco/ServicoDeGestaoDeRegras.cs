using CentralAntifraude.Application.Auditoria;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Tempo;

namespace CentralAntifraude.Application.Risco;

/// <summary>
/// Uma regra vista pela administracao: identidade, rascunho e historico.
///
/// <see cref="NoPerfilVigente"/> responde a pergunta que a tela precisa fazer:
/// "esta regra esta valendo agora?". Ela e diferente de <c>Ativa</c> — uma
/// regra pode estar ativa e ainda nao ter versao publicada, e portanto nao
/// participar do perfil.
/// </summary>
public sealed record RegraAdministrada(
    Regra Regra,
    VersaoDeRegra? VersaoVigente,
    IReadOnlyList<VersaoDeRegra> Versoes,
    bool NoPerfilVigente);

/// <summary>
/// A administracao de regras e do perfil de risco.
///
/// **Uma unica coisa muda o comportamento do motor: publicar uma versao de
/// perfil.** Publicar uma regra, desativar uma regra, reativar uma regra e
/// mexer nos limiares terminam todos publicando uma versao nova de perfil, na
/// mesma transacao. Sem essa regra, existiria um estado intermediario —
/// "versao publicada mas sem efeito" — e ninguem saberia dizer, olhando a
/// tela, o que o motor esta de fato executando.
///
/// **Nada aqui altera o que ja foi publicado.** Versao de regra e versao de
/// perfil nao tem metodo de alteracao e nao tem rota. Uma mudanca sempre
/// acrescenta; o passado continua explicavel pela versao que valia
/// (CLAUDE.md secoes 23 e 24).
///
/// **Concorrencia administrativa em duas camadas** (ROADMAP 8.6), como nos
/// casos: o token de versao da regra recusa a segunda edicao, e os indices
/// unicos <c>(regra, numero)</c> e <c>(perfil, numero)</c> arbitram duas
/// publicacoes que passem juntas pela primeira checagem.
/// </summary>
public sealed class ServicoDeGestaoDeRegras
{
    private readonly IRepositorioDeRisco _risco;
    private readonly IContextoDoUsuarioAtual _contextoAtual;
    private readonly IRegistradorDeAuditoria _auditoria;
    private readonly IUnidadeDeTrabalho _unidadeDeTrabalho;
    private readonly IRelogio _relogio;

    public ServicoDeGestaoDeRegras(
        IRepositorioDeRisco risco,
        IContextoDoUsuarioAtual contextoAtual,
        IRegistradorDeAuditoria auditoria,
        IUnidadeDeTrabalho unidadeDeTrabalho,
        IRelogio relogio)
    {
        _risco = risco;
        _contextoAtual = contextoAtual;
        _auditoria = auditoria;
        _unidadeDeTrabalho = unidadeDeTrabalho;
        _relogio = relogio;
    }

    // -----------------------------------------------------------------------
    // Leitura
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<RegraAdministrada>> ListarAsync(CancellationToken cancellationToken)
    {
        GarantirSupervisao();

        var regras = await _risco.ListarTodasAsRegrasAsync(cancellationToken);
        var vigente = await _risco.BuscarVersaoAtivaDoPerfilAsync(cancellationToken);

        var noPerfil = vigente is null
            ? []
            : vigente.VersoesDeRegra.ToDictionary(v => v.RegraId);

        var ultimas = await _risco.BuscarUltimasVersoesAsync(
            [.. regras.Select(r => r.Id)],
            cancellationToken);

        return
        [
            .. regras
                // Mesma ordem da tela de regras vigentes: tipo, depois nome.
                // Uma lista administrativa que muda de ordem entre dois
                // carregamentos faz o Supervisor perder o item que procurava.
                .OrderBy(r => r.Tipo)
                .ThenBy(r => r.Nome, StringComparer.OrdinalIgnoreCase)
                .Select(regra => new RegraAdministrada(
                    regra,
                    ultimas.TryGetValue(regra.Id, out var versao) ? versao : null,
                    [],
                    noPerfil.ContainsKey(regra.Id))),
        ];
    }

    public async Task<RegraAdministrada> ObterAsync(Guid regraId, CancellationToken cancellationToken)
    {
        GarantirSupervisao();

        var regra = await CarregarAsync(regraId, cancellationToken);
        var versoes = await _risco.ListarVersoesDaRegraAsync(regraId, cancellationToken);
        var vigente = await _risco.BuscarVersaoAtivaDoPerfilAsync(cancellationToken);

        return new RegraAdministrada(
            regra,
            versoes.Count > 0 ? versoes[0] : null,
            versoes,
            vigente is not null && vigente.VersoesDeRegra.Any(v => v.RegraId == regraId));
    }

    // -----------------------------------------------------------------------
    // Escrita
    // -----------------------------------------------------------------------

    /// <summary>
    /// Cria a regra como rascunho. Nada muda no motor ate a publicacao.
    /// </summary>
    public async Task<Regra> CriarAsync(
        TipoDeRegra tipo,
        string nome,
        ConfiguracaoDeRegra configuracao,
        int pontos,
        CancellationToken cancellationToken)
    {
        GarantirSupervisao();
        GarantirEntrada(nome, pontos);

        var regra = Traduzindo(() => Regra.Criar(
            _contextoAtual.OrganizacaoId,
            tipo,
            nome,
            configuracao,
            pontos,
            _relogio.Agora));

        await GarantirNomeLivreAsync(regra.Nome, regra.Id, cancellationToken);

        _risco.AdicionarRegra(regra);

        await AuditarAsync(
            OperacaoAuditada.RegraCriada,
            regra,
            $"Tipo: {tipo}; rascunho de {pontos} ponto(s)",
            cancellationToken);

        await SalvarAsync(cancellationToken);

        return regra;
    }

    public async Task<Regra> SalvarRascunhoAsync(
        Guid regraId,
        string nome,
        ConfiguracaoDeRegra configuracao,
        int pontos,
        int versaoEsperada,
        CancellationToken cancellationToken)
    {
        GarantirSupervisao();
        GarantirEntrada(nome, pontos);

        var regra = await CarregarParaEscritaAsync(regraId, versaoEsperada, cancellationToken);

        Traduzindo(() => regra.SalvarRascunho(nome, configuracao, pontos, _relogio.Agora));

        await GarantirNomeLivreAsync(regra.Nome, regra.Id, cancellationToken);

        await AuditarAsync(
            OperacaoAuditada.RascunhoDeRegraSalvo,
            regra,
            $"Rascunho de {pontos} ponto(s)",
            cancellationToken);

        await SalvarAsync(cancellationToken);

        return regra;
    }

    public async Task<Regra> DescartarRascunhoAsync(
        Guid regraId,
        int versaoEsperada,
        CancellationToken cancellationToken)
    {
        GarantirSupervisao();

        var regra = await CarregarParaEscritaAsync(regraId, versaoEsperada, cancellationToken);

        Traduzindo(() => regra.DescartarRascunho(_relogio.Agora));

        await AuditarAsync(
            OperacaoAuditada.RascunhoDeRegraDescartado,
            regra,
            "Rascunho descartado",
            cancellationToken);

        await SalvarAsync(cancellationToken);

        return regra;
    }

    /// <summary>
    /// Congela o rascunho em versao e publica um perfil novo com ela.
    ///
    /// As duas coisas na mesma transacao. Publicar a versao sem publicar o
    /// perfil deixaria uma versao "publicada" que o motor nao executa —
    /// e a tela teria que explicar a diferenca entre publicada e vigente.
    /// </summary>
    public async Task<VersaoDeRegra> PublicarAsync(
        Guid regraId,
        int versaoEsperada,
        CancellationToken cancellationToken)
    {
        GarantirSupervisao();

        var regra = await CarregarParaEscritaAsync(regraId, versaoEsperada, cancellationToken);

        var versoes = await _risco.ListarVersoesDaRegraAsync(regraId, cancellationToken);

        var nova = Traduzindo(() => regra.PublicarRascunho(
            versoes.Count > 0 ? versoes[0] : null,
            _relogio.Agora));

        _risco.AdicionarVersaoDeRegra(nova);

        var perfil = await SucederPerfilAsync(
            nova,
            null,
            null,
            $"Regra \"{regra.Nome}\" publicada na versao {nova.Numero}",
            cancellationToken);

        await AuditarAsync(
            OperacaoAuditada.VersaoDeRegraPublicada,
            regra,
            $"Versao {nova.Numero} com {nova.Pontos} ponto(s); perfil v{perfil.Numero}",
            cancellationToken);

        await SalvarAsync(cancellationToken);

        return nova;
    }

    /// <summary>
    /// Liga ou desliga a regra e publica o perfil correspondente.
    ///
    /// Desativar a ultima regra com versao publicada e recusado: o perfil
    /// ficaria sem regra nenhuma e o motor pontuaria tudo com zero, sem erro
    /// e sem aviso.
    /// </summary>
    public async Task<Regra> DefinirAtivacaoAsync(
        Guid regraId,
        bool ativa,
        int versaoEsperada,
        CancellationToken cancellationToken)
    {
        GarantirSupervisao();

        var regra = await CarregarParaEscritaAsync(regraId, versaoEsperada, cancellationToken);

        Traduzindo(() =>
        {
            if (ativa)
            {
                regra.Reativar(_relogio.Agora);
            }
            else
            {
                regra.Desativar(_relogio.Agora);
            }
        });

        // Uma regra sem versao publicada nunca esteve no perfil: ligar ou
        // desligar nao muda o que o motor executa, e publicar um perfil
        // identico so criaria ruido no historico.
        if (regra.FoiPublicada)
        {
            await SucederPerfilAsync(
                null,
                null,
                null,
                $"Regra \"{regra.Nome}\" {(ativa ? "reativada" : "desativada")}",
                cancellationToken);
        }

        await AuditarAsync(
            ativa ? OperacaoAuditada.RegraReativada : OperacaoAuditada.RegraDesativada,
            regra,
            ativa ? "Regra reativada" : "Regra desativada",
            cancellationToken);

        await SalvarAsync(cancellationToken);

        return regra;
    }

    /// <summary>
    /// Publica uma versao de perfil com limiares novos.
    ///
    /// <paramref name="numeroDaVersaoVigente"/> e o token de concorrencia do
    /// perfil: dois supervisores ajustando limiares ao mesmo tempo nao podem
    /// se sobrescrever em silencio.
    /// </summary>
    public async Task<VersaoDePerfilDeRisco> PublicarLimiaresAsync(
        int limiarDeRevisao,
        int limiarDeBloqueio,
        int numeroDaVersaoVigente,
        CancellationToken cancellationToken)
    {
        GarantirSupervisao();

        var vigente = await _risco.BuscarVersaoAtivaDoPerfilAsync(cancellationToken)
            ?? throw new RecursoNaoEncontrado("Perfil de risco");

        if (vigente.Numero != numeroDaVersaoVigente)
        {
            throw new ConflitoDeEstado(
                "versao_desatualizada",
                "O perfil mudou depois que voce o abriu. Recarregue para ver os limiares em vigor.");
        }

        var nova = await SucederPerfilAsync(
            null,
            limiarDeRevisao,
            limiarDeBloqueio,
            $"Limiares {vigente.LimiarDeRevisao}/{vigente.LimiarDeBloqueio} -> " +
            $"{limiarDeRevisao}/{limiarDeBloqueio}",
            cancellationToken);

        await SalvarAsync(cancellationToken);

        return nova;
    }

    // -----------------------------------------------------------------------
    // Apoio
    // -----------------------------------------------------------------------

    /// <summary>
    /// Compoe e adiciona a proxima versao do perfil.
    ///
    /// A composicao e sempre a mesma: a ultima versao publicada de cada regra
    /// ativa, mais os limiares. Nao ha "editar o perfil" — ha suceder o perfil
    /// por uma versao nova, que e o que preserva as avaliacoes anteriores.
    ///
    /// <paramref name="recemPublicada"/> existe porque a versao de regra criada
    /// nesta mesma operacao ainda nao esta no banco: sem ela, o perfil novo
    /// nasceria com a versao ANTERIOR da regra que acabou de ser publicada.
    /// </summary>
    private async Task<VersaoDePerfilDeRisco> SucederPerfilAsync(
        VersaoDeRegra? recemPublicada,
        int? limiarDeRevisao,
        int? limiarDeBloqueio,
        string motivo,
        CancellationToken cancellationToken)
    {
        var perfil = await _risco.BuscarPerfilAsync(cancellationToken)
            ?? throw new RecursoNaoEncontrado("Perfil de risco");

        var vigente = await _risco.BuscarVersaoAtivaDoPerfilAsync(cancellationToken);

        var regras = await _risco.ListarTodasAsRegrasAsync(cancellationToken);
        var ativas = regras.Where(r => r.Ativa).ToList();

        var ultimas = await _risco.BuscarUltimasVersoesAsync(
            [.. ativas.Select(r => r.Id)],
            cancellationToken);

        var versoes = new List<VersaoDeRegra>();

        foreach (var regra in ativas.OrderBy(r => r.Tipo).ThenBy(r => r.Id))
        {
            if (recemPublicada is not null && recemPublicada.RegraId == regra.Id)
            {
                versoes.Add(recemPublicada);
            }
            else if (ultimas.TryGetValue(regra.Id, out var versao))
            {
                versoes.Add(versao);
            }
        }

        var nova = Traduzindo(() => VersaoDePerfilDeRisco.Publicar(
            perfil,
            (vigente?.Numero ?? 0) + 1,
            limiarDeRevisao ?? vigente?.LimiarDeRevisao ?? CatalogoPadraoDeRisco.LimiarDeRevisao,
            limiarDeBloqueio ?? vigente?.LimiarDeBloqueio ?? CatalogoPadraoDeRisco.LimiarDeBloqueio,
            versoes,
            _relogio.Agora));

        _risco.AdicionarVersaoDePerfil(nova);

        // A trilha do PERFIL registra toda sucessao, e nao so a mudanca de
        // limiares. Sem isso, quem perguntasse "quando o perfil em vigor
        // mudou?" nao veria as trocas causadas por publicacao ou desativacao
        // de regra — que sao a maioria delas (ROADMAP 8.7).
        await AuditarPerfilAsync(nova, motivo, cancellationToken);

        return nova;
    }

    private async Task<Regra> CarregarAsync(Guid regraId, CancellationToken cancellationToken) =>
        await _risco.BuscarRegraPorIdAsync(regraId, cancellationToken)
        ?? throw new RecursoNaoEncontrado("Regra");

    /// <summary>
    /// Carrega a regra e confere a versao que o cliente leu.
    ///
    /// Primeira camada contra o lost update administrativo. A segunda sao os
    /// indices unicos de numero de versao, que seguram duas publicacoes que
    /// passem juntas por aqui.
    /// </summary>
    private async Task<Regra> CarregarParaEscritaAsync(
        Guid regraId,
        int versaoEsperada,
        CancellationToken cancellationToken)
    {
        var regra = await CarregarAsync(regraId, cancellationToken);

        if (regra.Versao != versaoEsperada)
        {
            throw new ConflitoDeEstado(
                "versao_desatualizada",
                "A regra mudou depois que voce a abriu. Recarregue para ver o que aconteceu antes de agir.");
        }

        return regra;
    }

    private async Task GarantirNomeLivreAsync(
        string nome,
        Guid regraId,
        CancellationToken cancellationToken)
    {
        // Duas regras com o mesmo nome no mesmo tenant tornariam a lista, a
        // auditoria e a explicacao de um sinal ambiguas — e desde a Fase 8 o
        // tipo ja nao distingue, porque duas regras do mesmo tipo sao
        // permitidas.
        if (await _risco.ExisteRegraComNomeAsync(nome, regraId, cancellationToken))
        {
            throw new ConflitoDeEstado(
                "nome_em_uso",
                $"Ja existe uma regra chamada \"{nome}\" nesta organizacao.");
        }
    }

    /// <summary>
    /// Nome e peso invalidos sao <c>400</c>, e nao <c>409</c>.
    ///
    /// O recurso nao esta num estado incompativel: o que veio e que nao serve.
    /// A regra de texto e a mesma do resto do produto — recusar marcacao, nunca
    /// limpar em silencio — porque o nome de uma regra tambem e exibido.
    /// </summary>
    private static void GarantirEntrada(string nome, int pontos)
    {
        if (!TextoDeUsuario.EhValido(
                nome,
                Regra.TamanhoMinimoDoNome,
                Regra.TamanhoMaximoDoNome,
                "nome",
                out _,
                out var erro))
        {
            throw new ErroDeValidacao("nome", erro);
        }

        if (pontos is < CatalogoDeTiposDeRegra.PontosMinimos or > VersaoDeRegra.PontosMaximos)
        {
            throw new ErroDeValidacao(
                "pontos",
                $"Pontos devem estar entre {CatalogoDeTiposDeRegra.PontosMinimos} e " +
                $"{VersaoDeRegra.PontosMaximos}.");
        }
    }

    private void GarantirSupervisao()
    {
        // Gerir regras e supervisao (CLAUDE.md secao 8.2). A politica da rota
        // ja recusa antes daqui; esta e a segunda camada, para o caso de uma
        // rota futura esquecer a politica.
        if (_contextoAtual.Perfil
            is not (PerfilDeUsuario.Administrador or PerfilDeUsuario.SupervisorDeFraude))
        {
            throw new NaoAutorizado("Gerir regras de risco e uma acao de supervisao.");
        }
    }

    /// <summary>
    /// Grava, traduzindo a corrida perdida em conflito.
    ///
    /// Duas publicacoes simultaneas chegam ao banco com o mesmo numero de
    /// versao. Quem perde esbarra em <c>(regra, numero)</c> ou em
    /// <c>(perfil, numero)</c> — e o cliente nao deveria receber <c>500</c>
    /// quando a resposta certa e "outra pessoa publicou antes; recarregue".
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
                "conflito_de_publicacao",
                "Outra pessoa publicou antes de voce. Recarregue as regras para ver o que mudou.",
                excecao);
        }
    }

    private Task AuditarAsync(
        OperacaoAuditada operacao,
        Regra regra,
        string detalhe,
        CancellationToken cancellationToken) =>
        _auditoria.RegistrarAsync(
            RegistroDeAuditoria.Registrar(
                regra.OrganizacaoId,
                operacao,
                nameof(Regra),
                _relogio.Agora,
                autorId: _contextoAtual.UsuarioId,
                entidadeId: regra.Id.ToString(),
                detalhe: $"{regra.Nome} — {detalhe}"),
            cancellationToken);

    private Task AuditarPerfilAsync(
        VersaoDePerfilDeRisco versao,
        string detalhe,
        CancellationToken cancellationToken) =>
        _auditoria.RegistrarAsync(
            RegistroDeAuditoria.Registrar(
                versao.OrganizacaoId,
                OperacaoAuditada.VersaoDePerfilPublicada,
                nameof(PerfilDeRisco),
                _relogio.Agora,
                autorId: _contextoAtual.UsuarioId,
                entidadeId: versao.PerfilId.ToString(),
                detalhe: $"Versao {versao.Numero} — {detalhe}"),
            cancellationToken);

    /// <summary>
    /// Traduz a recusa do dominio para o contrato HTTP.
    ///
    /// Publicar um rascunho que nao existe, ou desativar a ultima regra, nao e
    /// defeito do servidor: e o estado do recurso recusando a operacao.
    /// </summary>
    private static T Traduzindo<T>(Func<T> operacao)
    {
        try
        {
            return operacao();
        }
        catch (ViolacaoDeInvariante excecao)
        {
            throw new ConflitoDeEstado("estado_da_regra", excecao.Message);
        }
    }

    private static void Traduzindo(Action operacao) =>
        Traduzindo<object?>(() =>
        {
            operacao();
            return null;
        });
}
