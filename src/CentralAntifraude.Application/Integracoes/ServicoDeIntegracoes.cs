using CentralAntifraude.Application.Auditoria;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Correlacao;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Integracoes;
using CentralAntifraude.Domain.Tempo;

namespace CentralAntifraude.Application.Integracoes;

/// <summary>
/// Administracao de integracoes pelo Administrador da organizacao.
///
/// Como em <c>ServicoDeUsuarios</c>, nenhum metodo recebe identificador de
/// organizacao: o tenant vem do contexto autenticado, e nao ha por onde passar
/// o errado.
/// </summary>
public sealed class ServicoDeIntegracoes
{
    public static readonly string[] CamposDeOrdenacao = ["nome", "criadaEm"];

    public const string CampoDeOrdenacaoPadrao = "nome";

    /// <summary>
    /// Teto de credenciais vivas por integracao.
    ///
    /// Duas bastam para rotacao sem interrupcao: a antiga continua valendo
    /// enquanto o integrador troca a configuracao. Um numero maior so
    /// aumentaria a superficie de credenciais esquecidas e validas.
    /// </summary>
    public const int MaximoDeCredenciaisAtivas = 2;

    private readonly IRepositorioDeIntegracoes _integracoes;
    private readonly IProtetorDeCredencial _protetor;
    private readonly IRegistradorDeAuditoria _auditoria;
    private readonly IContextoDoUsuarioAtual _contexto;
    private readonly IContextoDeCorrelacao _correlacao;
    private readonly IUnidadeDeTrabalho _unidadeDeTrabalho;
    private readonly IRelogio _relogio;

    public ServicoDeIntegracoes(
        IRepositorioDeIntegracoes integracoes,
        IProtetorDeCredencial protetor,
        IRegistradorDeAuditoria auditoria,
        IContextoDoUsuarioAtual contexto,
        IContextoDeCorrelacao correlacao,
        IUnidadeDeTrabalho unidadeDeTrabalho,
        IRelogio relogio)
    {
        _integracoes = integracoes;
        _protetor = protetor;
        _auditoria = auditoria;
        _contexto = contexto;
        _correlacao = correlacao;
        _unidadeDeTrabalho = unidadeDeTrabalho;
        _relogio = relogio;
    }

    public Task<Pagina<Integracao>> ListarAsync(
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken) =>
        _integracoes.ListarAsync(paginacao, ordenacao, cancellationToken);

    public async Task<Integracao> ObterAsync(Guid integracaoId, CancellationToken cancellationToken) =>
        await _integracoes.BuscarPorIdAsync(integracaoId, cancellationToken)
        ?? throw new RecursoNaoEncontrado("Integracao");

    public Task<IReadOnlyList<CredencialDeIntegracao>> ListarCredenciaisAsync(
        Guid integracaoId,
        CancellationToken cancellationToken) =>
        _integracoes.ListarCredenciaisAsync(integracaoId, cancellationToken);

    /// <summary>
    /// Cria a integracao e emite a primeira credencial.
    ///
    /// O valor bruto volta apenas nesta resposta. Nao ha endpoint que o
    /// mostre de novo — se ele se perder, o caminho e rotacionar.
    /// </summary>
    public async Task<(Integracao Integracao, CredencialEmitida Credencial)> CriarAsync(
        string? nome,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;

        if (string.IsNullOrWhiteSpace(nome) || nome.Trim().Length > Integracao.TamanhoMaximoDoNome)
        {
            throw new ErroDeValidacao(
                "nome",
                $"Nome e obrigatorio e deve ter no maximo {Integracao.TamanhoMaximoDoNome} caracteres.");
        }

        var integracao = Integracao.Criar(_contexto.OrganizacaoId, nome, agora);
        _integracoes.Adicionar(integracao);

        var credencial = EmitirCredencial(integracao, agora);

        await AuditarAsync(
            OperacaoAuditada.IntegracaoCriada,
            integracao,
            "Credencial inicial emitida.",
            agora,
            cancellationToken);

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return (integracao, credencial);
    }

    public async Task<Integracao> RenomearAsync(
        Guid integracaoId,
        string? novoNome,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;

        if (string.IsNullOrWhiteSpace(novoNome) || novoNome.Trim().Length > Integracao.TamanhoMaximoDoNome)
        {
            throw new ErroDeValidacao(
                "nome",
                $"Nome e obrigatorio e deve ter no maximo {Integracao.TamanhoMaximoDoNome} caracteres.");
        }

        var integracao = await ObterAsync(integracaoId, cancellationToken);
        integracao.Renomear(novoNome, agora);

        await AuditarAsync(OperacaoAuditada.IntegracaoAlterada, integracao, "Nome alterado.", agora, cancellationToken);
        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return integracao;
    }

    /// <summary>
    /// Emite uma credencial nova, mantendo as existentes validas.
    ///
    /// E o que permite rotacionar sem derrubar a ingestao do cliente: a nova
    /// e distribuida, o integrador troca a configuracao quando puder, e so
    /// entao a antiga e revogada.
    /// </summary>
    public async Task<CredencialEmitida> RotacionarCredencialAsync(
        Guid integracaoId,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;
        var integracao = await ObterAsync(integracaoId, cancellationToken);

        var ativas = (await _integracoes.ListarCredenciaisAsync(integracaoId, cancellationToken))
            .Count(c => !c.EstaRevogada);

        if (ativas >= MaximoDeCredenciaisAtivas)
        {
            throw new ConflitoDeEstado(
                "limite_de_credenciais_atingido",
                $"Esta integracao ja possui {MaximoDeCredenciaisAtivas} credenciais ativas. " +
                "Revogue a que nao esta mais em uso antes de emitir outra.");
        }

        var credencial = EmitirCredencial(integracao, agora);

        await AuditarAsync(
            OperacaoAuditada.CredencialDeIntegracaoEmitida,
            integracao,
            "Credencial adicional emitida para rotacao.",
            agora,
            cancellationToken);

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return credencial;
    }

    public async Task RevogarCredencialAsync(
        Guid integracaoId,
        Guid credencialId,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;
        var integracao = await ObterAsync(integracaoId, cancellationToken);

        var credencial = (await _integracoes.ListarCredenciaisAsync(integracaoId, cancellationToken))
            .FirstOrDefault(c => c.Id == credencialId)
            ?? throw new RecursoNaoEncontrado("Credencial");

        if (credencial.EstaRevogada)
        {
            return;
        }

        credencial.Revogar(MotivoDeRevogacaoDeCredencial.RevogadaManualmente, agora);

        await AuditarAsync(
            OperacaoAuditada.CredencialDeIntegracaoRevogada,
            integracao,
            $"Credencial {credencial.IdentificadorPublico} revogada.",
            agora,
            cancellationToken);

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);
    }

    public async Task<Integracao> DefinirAtivacaoAsync(
        Guid integracaoId,
        bool ativa,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;
        var integracao = await ObterAsync(integracaoId, cancellationToken);

        if (integracao.Ativa == ativa)
        {
            return integracao;
        }

        if (ativa)
        {
            integracao.Reativar(agora);
            await AuditarAsync(OperacaoAuditada.IntegracaoReativada, integracao, null, agora, cancellationToken);
        }
        else
        {
            integracao.Desativar(agora);

            // Desativar a integracao revoga as credenciais dela. Deixa-las
            // vivas significaria que reativar devolveria acesso a chaves que
            // podem ter circulado enquanto isso.
            foreach (var credencial in await _integracoes.ListarCredenciaisAsync(integracaoId, cancellationToken))
            {
                credencial.Revogar(MotivoDeRevogacaoDeCredencial.IntegracaoDesativada, agora);
            }

            await AuditarAsync(
                OperacaoAuditada.IntegracaoDesativada,
                integracao,
                "Credenciais revogadas junto.",
                agora,
                cancellationToken);
        }

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return integracao;
    }

    private CredencialEmitida EmitirCredencial(Integracao integracao, DateTimeOffset agora)
    {
        var (identificadorPublico, hashDoSegredo, valorBruto) = _protetor.Gerar();

        var credencial = CredencialDeIntegracao.Criar(
            integracao.OrganizacaoId,
            integracao.Id,
            identificadorPublico,
            hashDoSegredo,
            agora);

        _integracoes.AdicionarCredencial(credencial);

        return new CredencialEmitida(credencial, valorBruto);
    }

    private Task AuditarAsync(
        OperacaoAuditada operacao,
        Integracao alvo,
        string? detalhe,
        DateTimeOffset agora,
        CancellationToken cancellationToken) =>
        _auditoria.RegistrarAsync(
            RegistroDeAuditoria.Registrar(
                _contexto.OrganizacaoId,
                operacao,
                entidade: nameof(Integracao),
                agora,
                autorId: _contexto.UsuarioId,
                autorDescricao: _contexto.UsuarioId.ToString(),
                entidadeId: alvo.Id.ToString(),
                // O detalhe cita, no maximo, o identificador PUBLICO da
                // credencial. O segredo nunca chega perto da trilha.
                detalhe: detalhe,
                idDeCorrelacao: _correlacao.IdDeCorrelacao),
            cancellationToken);
}
