using CentralAntifraude.Application.Auditoria;
using CentralAntifraude.Application.Correlacao;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Tempo;

namespace CentralAntifraude.Application.Identidade;

/// <summary>Access token assinado e seu prazo.</summary>
public sealed record AccessTokenEmitido(string Token, DateTimeOffset ExpiraEm);

/// <summary>Emissao do access token. Implementado na Infrastructure.</summary>
public interface IEmissorDeAccessToken
{
    AccessTokenEmitido Emitir(Usuario usuario, DateTimeOffset agora);
}

/// <summary>
/// Geracao e hash do refresh token.
///
/// O token e opaco e aleatorio, nao um JWT: ele nao precisa carregar
/// informacao nenhuma, e um valor sem significado nao pode ser lido nem
/// forjado — so conferido contra a linha do banco.
/// </summary>
public interface IProtetorDeRefreshToken
{
    /// <summary>Gera um valor novo e devolve o bruto (entregue uma unica vez) e o hash.</summary>
    (string Bruto, string Hash) Gerar();

    string CalcularHash(string tokenBruto);
}

/// <summary>Sessao recem-estabelecida ou renovada.</summary>
public sealed record SessaoEmitida(
    string AccessToken,
    DateTimeOffset AccessTokenExpiraEm,
    string RefreshTokenBruto,
    DateTimeOffset RefreshTokenExpiraEm,
    Usuario Usuario);

/// <summary>
/// Login, renovacao e encerramento de sessao humana.
///
/// Tres invariantes de seguranca moram aqui:
///
/// 1. **Nenhuma resposta revela se um e-mail existe.** Credencial errada,
///    usuario inexistente, usuario desativado e organizacao desativada
///    produzem exatamente a mesma falha.
///
/// 2. **O tempo de resposta tambem nao revela.** Quando o e-mail nao existe,
///    o servico ainda assim gasta o custo de uma verificacao de senha. Sem
///    isso, a diferenca de latencia entre "existe" e "nao existe" seria um
///    oraculo de enumeracao de usuarios.
///
/// 3. **Reuso de refresh token derruba a familia inteira.** Ver
///    <see cref="RefreshToken"/>.
/// </summary>
public sealed class ServicoDeAutenticacao
{
    /// <summary>
    /// Hash descartavel usado para gastar tempo quando o usuario nao existe.
    /// Gerado uma vez por processo a partir de um valor aleatorio: nao e
    /// senha de ninguem e nunca confere com nada.
    /// </summary>
    private readonly Lazy<string> _hashDeComparacaoFicticia;

    private readonly IRepositorioDeUsuarios _usuarios;
    private readonly IRepositorioDeOrganizacoes _organizacoes;
    private readonly IRepositorioDeRefreshTokens _refreshTokens;
    private readonly IHashDeSenha _hashDeSenha;
    private readonly IEmissorDeAccessToken _emissorDeAccessToken;
    private readonly IProtetorDeRefreshToken _protetorDeRefreshToken;
    private readonly IRegistradorDeAuditoria _auditoria;
    private readonly IContextoDeCorrelacao _correlacao;
    private readonly IUnidadeDeTrabalho _unidadeDeTrabalho;
    private readonly IRelogio _relogio;
    private readonly OpcoesDeAutenticacao _opcoes;

    public ServicoDeAutenticacao(
        IRepositorioDeUsuarios usuarios,
        IRepositorioDeOrganizacoes organizacoes,
        IRepositorioDeRefreshTokens refreshTokens,
        IHashDeSenha hashDeSenha,
        IEmissorDeAccessToken emissorDeAccessToken,
        IProtetorDeRefreshToken protetorDeRefreshToken,
        IRegistradorDeAuditoria auditoria,
        IContextoDeCorrelacao correlacao,
        IUnidadeDeTrabalho unidadeDeTrabalho,
        IRelogio relogio,
        OpcoesDeAutenticacao opcoes)
    {
        _usuarios = usuarios;
        _organizacoes = organizacoes;
        _refreshTokens = refreshTokens;
        _hashDeSenha = hashDeSenha;
        _emissorDeAccessToken = emissorDeAccessToken;
        _protetorDeRefreshToken = protetorDeRefreshToken;
        _auditoria = auditoria;
        _correlacao = correlacao;
        _unidadeDeTrabalho = unidadeDeTrabalho;
        _relogio = relogio;
        _opcoes = opcoes;

        _hashDeSenha = hashDeSenha;
        _hashDeComparacaoFicticia = new Lazy<string>(
            () => _hashDeSenha.Gerar(Convert.ToBase64String(Identificador.Novo().ToByteArray())));
    }

    public async Task<SessaoEmitida> AutenticarAsync(
        string? email,
        string? senha,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;

        // Entrada malformada segue o mesmo caminho de credencial errada: um
        // 400 aqui distinguiria "e-mail nao existe" de "e-mail invalido".
        if (!Email.TentarCriar(email, out var emailNormalizado, out _) ||
            string.IsNullOrEmpty(senha))
        {
            GastarTempoDeVerificacao();
            throw CredenciaisInvalidas();
        }

        var usuario = await _usuarios.BuscarPorEmailIgnorandoTenantAsync(emailNormalizado, cancellationToken);

        if (usuario is null)
        {
            // Sem usuario nao ha organizacao a que atribuir o registro, e a
            // trilha de auditoria e por tenant. A tentativa vira log
            // estruturado na borda, nao registro de auditoria.
            GastarTempoDeVerificacao();
            throw CredenciaisInvalidas();
        }

        var verificacao = _hashDeSenha.Verificar(usuario.HashDaSenha, senha);

        if (verificacao == ResultadoDaVerificacaoDeSenha.Invalida)
        {
            await AuditarAsync(
                usuario.OrganizacaoId,
                OperacaoAuditada.LoginRecusado,
                usuario,
                "Senha invalida.",
                agora,
                cancellationToken);

            await _unidadeDeTrabalho.SalvarAsync(cancellationToken);
            throw CredenciaisInvalidas();
        }

        var organizacao = await _organizacoes.BuscarPorIdAsync(usuario.OrganizacaoId, cancellationToken);

        if (!usuario.Ativo || organizacao is null || !organizacao.Ativa)
        {
            await AuditarAsync(
                usuario.OrganizacaoId,
                OperacaoAuditada.LoginRecusado,
                usuario,
                usuario.Ativo ? "Organizacao inativa." : "Usuario inativo.",
                agora,
                cancellationToken);

            await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

            // Mesma falha de credencial errada: dizer "sua conta esta
            // desativada" confirmaria ao atacante que a conta existe e que a
            // senha estava certa.
            throw CredenciaisInvalidas();
        }

        // Unica oportunidade de regravar o hash: e aqui que a senha em claro
        // existe. Se os parametros de custo subiram, o usuario e migrado no
        // proximo login, sem precisar trocar de senha.
        if (verificacao == ResultadoDaVerificacaoDeSenha.ValidaMasPrecisaRegravar)
        {
            usuario.DefinirSenha(_hashDeSenha.Gerar(senha), agora);
        }

        var token = _protetorDeRefreshToken.Gerar();

        _refreshTokens.Adicionar(RefreshToken.IniciarFamilia(
            usuario.OrganizacaoId,
            usuario.Id,
            token.Hash,
            agora,
            _opcoes.ValidadeDoRefreshToken));

        await AuditarAsync(
            usuario.OrganizacaoId,
            OperacaoAuditada.LoginBemSucedido,
            usuario,
            detalhe: null,
            agora,
            cancellationToken);

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return MontarSessao(usuario, token.Bruto, agora);
    }

    /// <summary>
    /// Entra na conta de demonstracao sem senha.
    ///
    /// **Nao e um atalho do login — e um caminho proprio, com uma trava.** Ele
    /// so funciona quando o ambiente marca <see cref="OpcoesDeAutenticacao.DemoHabilitado"/>;
    /// numa instalacao real esse campo e `false` e este metodo responde como se
    /// o recurso nao existisse, sem revelar que existe um caminho de demo.
    ///
    /// Quem escolhe a conta e o SERVIDOR (<see cref="OpcoesDeAutenticacao.EmailDaContaDemo"/>),
    /// e nunca o cliente. Um endpoint que recebesse o e-mail viraria uma porta
    /// para entrar em qualquer conta cujo e-mail se conheca. E a conta e um
    /// Analista de proposito: um visitante ve a operacao, mas nao administra.
    ///
    /// Fora a ausencia da senha, o fluxo e o mesmo do login: emite refresh
    /// token, registra na auditoria e monta a sessao. A diferenca de operacao
    /// auditada — <see cref="OperacaoAuditada.LoginDeDemonstracao"/> — deixa a
    /// trilha distinguir um acesso de vitrine de um login de verdade.
    /// </summary>
    public async Task<SessaoEmitida> EntrarComoDemoAsync(CancellationToken cancellationToken)
    {
        if (!_opcoes.DemoHabilitado)
        {
            throw new RecursoNaoEncontrado("Recurso");
        }

        var agora = _relogio.Agora;

        if (!Email.TentarCriar(_opcoes.EmailDaContaDemo, out var emailDemo, out _))
        {
            // Configuracao errada e falha do servidor, nao do visitante: um
            // e-mail de demo malformado e problema de quem implantou.
            throw new ConflitoDeEstado(
                "demo_mal_configurada",
                "A conta de demonstracao esta mal configurada.");
        }

        var usuario = await _usuarios.BuscarPorEmailIgnorandoTenantAsync(emailDemo, cancellationToken);

        var organizacao = usuario is null
            ? null
            : await _organizacoes.BuscarPorIdAsync(usuario.OrganizacaoId, cancellationToken);

        if (usuario is null || organizacao is null || !usuario.Ativo || !organizacao.Ativa)
        {
            // Demo habilitada mas conta ausente/inativa e, de novo, erro de
            // implantacao — o seed narrativo nao rodou. Nao e 401: o visitante
            // nao errou credencial nenhuma.
            throw new ConflitoDeEstado(
                "demo_indisponivel",
                "A conta de demonstracao nao esta disponivel.");
        }

        var token = _protetorDeRefreshToken.Gerar();

        _refreshTokens.Adicionar(RefreshToken.IniciarFamilia(
            usuario.OrganizacaoId,
            usuario.Id,
            token.Hash,
            agora,
            _opcoes.ValidadeDoRefreshToken));

        await AuditarAsync(
            usuario.OrganizacaoId,
            OperacaoAuditada.LoginDeDemonstracao,
            usuario,
            detalhe: null,
            agora,
            cancellationToken);

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return MontarSessao(usuario, token.Bruto, agora);
    }

    public async Task<SessaoEmitida> RenovarAsync(
        string? refreshTokenBruto,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;

        if (string.IsNullOrWhiteSpace(refreshTokenBruto))
        {
            throw SessaoInvalida();
        }

        var hash = _protetorDeRefreshToken.CalcularHash(refreshTokenBruto);
        var token = await _refreshTokens.BuscarPorHashAsync(hash, cancellationToken);

        if (token is null)
        {
            throw SessaoInvalida();
        }

        // Um token ja usado reaparecendo significa que existem duas copias em
        // circulacao e nao ha como saber qual e a legitima. A unica resposta
        // segura e derrubar a familia inteira e exigir novo login.
        if (token.JaFoiUsado)
        {
            await DerrubarFamiliaAsync(token, agora, cancellationToken);
            throw SessaoInvalida();
        }

        if (!token.PodeSerUsado(agora))
        {
            throw SessaoInvalida();
        }

        var usuario = await _usuarios.BuscarPorIdIgnorandoTenantAsync(token.UsuarioId, cancellationToken);
        var organizacao = usuario is null
            ? null
            : await _organizacoes.BuscarPorIdAsync(usuario.OrganizacaoId, cancellationToken);

        // Desativar um usuario precisa cortar as sessoes abertas na hora.
        // Sem esta verificacao, um demitido continuaria renovando a sessao
        // ate o refresh token expirar sozinho.
        if (usuario is null || !usuario.Ativo || organizacao is null || !organizacao.Ativa)
        {
            await RevogarFamiliaAsync(token.FamiliaId, MotivoDeRevogacao.AcessoRevogado, agora, cancellationToken);
            await _unidadeDeTrabalho.SalvarAsync(cancellationToken);
            throw SessaoInvalida();
        }

        token.MarcarComoUsado(agora);

        var novoToken = _protetorDeRefreshToken.Gerar();
        _refreshTokens.Adicionar(token.Suceder(novoToken.Hash, agora, _opcoes.ValidadeDoRefreshToken));

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return MontarSessao(usuario, novoToken.Bruto, agora);
    }

    /// <summary>
    /// Encerra a sessao revogando a familia inteira do token apresentado.
    ///
    /// Nao lanca quando o token e desconhecido ou ja invalido: o resultado
    /// pedido — "esta sessao nao vale mais" — ja e verdade, e devolver erro
    /// so daria a um atacante uma forma de testar se um token existe.
    /// </summary>
    public async Task EncerrarSessaoAsync(
        string? refreshTokenBruto,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;

        if (string.IsNullOrWhiteSpace(refreshTokenBruto))
        {
            return;
        }

        var hash = _protetorDeRefreshToken.CalcularHash(refreshTokenBruto);
        var token = await _refreshTokens.BuscarPorHashAsync(hash, cancellationToken);

        if (token is null)
        {
            return;
        }

        await RevogarFamiliaAsync(token.FamiliaId, MotivoDeRevogacao.Logout, agora, cancellationToken);

        var usuario = await _usuarios.BuscarPorIdIgnorandoTenantAsync(token.UsuarioId, cancellationToken);

        if (usuario is not null)
        {
            await AuditarAsync(
                usuario.OrganizacaoId,
                OperacaoAuditada.Logout,
                usuario,
                detalhe: null,
                agora,
                cancellationToken);
        }

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);
    }

    /// <summary>
    /// Revoga todo o acesso vivo de um usuario. Chamado quando um
    /// administrador desativa a conta ou muda o perfil — um perfil novo nao
    /// pode conviver com um access token que ainda carrega o antigo.
    /// </summary>
    public async Task RevogarAcessoDoUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;
        var ativos = await _refreshTokens.ListarAtivosDoUsuarioAsync(usuarioId, cancellationToken);

        foreach (var token in ativos)
        {
            token.Revogar(MotivoDeRevogacao.AcessoRevogado, agora);
        }
    }

    private async Task DerrubarFamiliaAsync(
        RefreshToken token,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        await RevogarFamiliaAsync(token.FamiliaId, MotivoDeRevogacao.ReusoDetectado, agora, cancellationToken);

        var usuario = await _usuarios.BuscarPorIdIgnorandoTenantAsync(token.UsuarioId, cancellationToken);

        if (usuario is not null)
        {
            await AuditarAsync(
                usuario.OrganizacaoId,
                OperacaoAuditada.ReusoDeRefreshTokenDetectado,
                usuario,
                "Refresh token ja utilizado foi apresentado novamente. Familia revogada.",
                agora,
                cancellationToken);
        }

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);
    }

    private async Task RevogarFamiliaAsync(
        Guid familiaId,
        MotivoDeRevogacao motivo,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        var ativos = await _refreshTokens.ListarAtivosDaFamiliaAsync(familiaId, cancellationToken);

        foreach (var ativo in ativos)
        {
            ativo.Revogar(motivo, agora);
        }
    }

    private SessaoEmitida MontarSessao(Usuario usuario, string refreshTokenBruto, DateTimeOffset agora)
    {
        var acesso = _emissorDeAccessToken.Emitir(usuario, agora);

        return new SessaoEmitida(
            acesso.Token,
            acesso.ExpiraEm,
            refreshTokenBruto,
            agora + _opcoes.ValidadeDoRefreshToken,
            usuario);
    }

    private Task AuditarAsync(
        Guid organizacaoId,
        OperacaoAuditada operacao,
        Usuario usuario,
        string? detalhe,
        DateTimeOffset agora,
        CancellationToken cancellationToken) =>
        _auditoria.RegistrarAsync(
            RegistroDeAuditoria.Registrar(
                organizacaoId,
                operacao,
                entidade: nameof(Usuario),
                agora,
                autorId: usuario.Id,
                autorDescricao: usuario.Email.Valor,
                entidadeId: usuario.Id.ToString(),
                detalhe: detalhe,
                idDeCorrelacao: _correlacao.IdDeCorrelacao),
            cancellationToken);

    private void GastarTempoDeVerificacao() =>
        _hashDeSenha.Verificar(_hashDeComparacaoFicticia.Value, "senha-que-nunca-confere");

    private static NaoAutenticado CredenciaisInvalidas() =>
        new NaoAutenticado("credenciais_invalidas", "E-mail ou senha invalidos.");

    private static NaoAutenticado SessaoInvalida() =>
        new NaoAutenticado("sessao_invalida", "Sessao invalida ou expirada. Entre novamente.");
}
