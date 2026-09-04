using CentralAntifraude.Application.Auditoria;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Correlacao;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Tempo;

namespace CentralAntifraude.Application.Identidade;

/// <summary>
/// Gestao de usuarios pelo Administrador da organizacao.
///
/// Tudo aqui opera dentro do tenant do usuario autenticado, que vem de
/// <see cref="IContextoDoUsuarioAtual"/> e nunca de parametro. Nenhum metodo
/// recebe um identificador de organizacao — nao ha como passar o tenant
/// errado, porque nao ha por onde passar.
/// </summary>
public sealed class ServicoDeUsuarios
{
    /// <summary>
    /// Campos pelos quais a listagem pode ser ordenada.
    /// Lista fechada: o cliente escolhe entre estes, e nunca informa um nome
    /// de coluna livre (ver ADR 0004).
    /// </summary>
    public static readonly string[] CamposDeOrdenacao = ["nome", "email", "perfil", "criadoEm"];

    public const string CampoDeOrdenacaoPadrao = "nome";

    /// <summary>
    /// Comprimento minimo de senha.
    ///
    /// A OWASP recomenda priorizar tamanho a regras de composicao, que
    /// empurram as pessoas para padroes previsiveis do tipo "Senha@123".
    /// </summary>
    public const int TamanhoMinimoDaSenha = 12;

    public const int TamanhoMaximoDaSenha = 256;

    private readonly IRepositorioDeUsuarios _usuarios;
    private readonly IHashDeSenha _hashDeSenha;
    private readonly IRegistradorDeAuditoria _auditoria;
    private readonly IContextoDoUsuarioAtual _contexto;
    private readonly IContextoDeCorrelacao _correlacao;
    private readonly IUnidadeDeTrabalho _unidadeDeTrabalho;
    private readonly ServicoDeAutenticacao _autenticacao;
    private readonly IRelogio _relogio;

    public ServicoDeUsuarios(
        IRepositorioDeUsuarios usuarios,
        IHashDeSenha hashDeSenha,
        IRegistradorDeAuditoria auditoria,
        IContextoDoUsuarioAtual contexto,
        IContextoDeCorrelacao correlacao,
        IUnidadeDeTrabalho unidadeDeTrabalho,
        ServicoDeAutenticacao autenticacao,
        IRelogio relogio)
    {
        _usuarios = usuarios;
        _hashDeSenha = hashDeSenha;
        _auditoria = auditoria;
        _contexto = contexto;
        _correlacao = correlacao;
        _unidadeDeTrabalho = unidadeDeTrabalho;
        _autenticacao = autenticacao;
        _relogio = relogio;
    }

    public Task<Pagina<Usuario>> ListarAsync(
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken) =>
        _usuarios.ListarAsync(paginacao, ordenacao, cancellationToken);

    public async Task<Usuario> ObterAsync(Guid usuarioId, CancellationToken cancellationToken) =>
        await _usuarios.BuscarPorIdAsync(usuarioId, cancellationToken)
        ?? throw new RecursoNaoEncontrado("Usuario");

    public async Task<Usuario> CriarAsync(
        string? email,
        string? nomeCompleto,
        string? senha,
        PerfilDeUsuario perfil,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;

        if (!Email.TentarCriar(email, out var emailNormalizado, out var erroDeEmail))
        {
            throw new ErroDeValidacao("email", erroDeEmail);
        }

        ValidarSenha(senha);

        if (!Enum.IsDefined(perfil))
        {
            throw new ErroDeValidacao("perfil", "Perfil desconhecido.");
        }

        if (await _usuarios.ExisteEmailIgnorandoTenantAsync(emailNormalizado, cancellationToken))
        {
            // Conflito e nao validacao: o pedido esta bem formado, o estado
            // atual e que nao permite. E um administrador criando usuario ja
            // sabe que a organizacao dele existe, entao nao ha vazamento novo.
            throw new ConflitoDeEstado("email_ja_utilizado", "Ja existe um usuario com este e-mail.");
        }

        var usuario = Usuario.Criar(
            _contexto.OrganizacaoId,
            emailNormalizado,
            nomeCompleto ?? string.Empty,
            _hashDeSenha.Gerar(senha!),
            perfil,
            agora);

        _usuarios.Adicionar(usuario);

        await AuditarAsync(
            OperacaoAuditada.UsuarioCriado,
            usuario,
            $"Perfil: {perfil}.",
            agora,
            cancellationToken);

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return usuario;
    }

    public async Task<Usuario> AlterarPerfilAsync(
        Guid usuarioId,
        PerfilDeUsuario novoPerfil,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;

        if (!Enum.IsDefined(novoPerfil))
        {
            throw new ErroDeValidacao("perfil", "Perfil desconhecido.");
        }

        var usuario = await ObterAsync(usuarioId, cancellationToken);

        // Um administrador rebaixando a si mesmo poderia deixar a organizacao
        // sem ninguem capaz de gerir usuarios — estado do qual nao ha saida
        // pela propria aplicacao.
        if (usuario.Id == _contexto.UsuarioId && novoPerfil != PerfilDeUsuario.Administrador)
        {
            throw new ConflitoDeEstado(
                "administrador_nao_pode_rebaixar_a_si_mesmo",
                "Um administrador nao pode remover o proprio perfil de administrador.");
        }

        var perfilAnterior = usuario.Perfil;

        if (perfilAnterior == novoPerfil)
        {
            return usuario;
        }

        usuario.AlterarPerfil(novoPerfil, agora);

        // O perfil viaja dentro do access token. Sem revogar as sessoes, o
        // usuario continuaria agindo com o perfil antigo ate o token expirar.
        await _autenticacao.RevogarAcessoDoUsuarioAsync(usuario.Id, cancellationToken);

        await AuditarAsync(
            OperacaoAuditada.PerfilDeUsuarioAlterado,
            usuario,
            $"Perfil: {perfilAnterior} -> {novoPerfil}.",
            agora,
            cancellationToken);

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return usuario;
    }

    public async Task<Usuario> AlterarNomeAsync(
        Guid usuarioId,
        string? novoNome,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;

        if (string.IsNullOrWhiteSpace(novoNome) || novoNome.Trim().Length > Usuario.TamanhoMaximoDoNome)
        {
            throw new ErroDeValidacao(
                "nomeCompleto",
                $"Nome e obrigatorio e deve ter no maximo {Usuario.TamanhoMaximoDoNome} caracteres.");
        }

        var usuario = await ObterAsync(usuarioId, cancellationToken);
        usuario.AlterarNome(novoNome, agora);

        await AuditarAsync(OperacaoAuditada.UsuarioAlterado, usuario, "Nome alterado.", agora, cancellationToken);
        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return usuario;
    }

    public async Task<Usuario> DefinirAtivacaoAsync(
        Guid usuarioId,
        bool ativo,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;
        var usuario = await ObterAsync(usuarioId, cancellationToken);

        // Desativar a si mesmo deixaria o administrador do lado de fora sem
        // ninguem para reativa-lo.
        if (usuario.Id == _contexto.UsuarioId && !ativo)
        {
            throw new ConflitoDeEstado(
                "administrador_nao_pode_se_desativar",
                "Um administrador nao pode desativar a propria conta.");
        }

        if (usuario.Ativo == ativo)
        {
            return usuario;
        }

        if (ativo)
        {
            usuario.Reativar(agora);
            await AuditarAsync(OperacaoAuditada.UsuarioReativado, usuario, null, agora, cancellationToken);
        }
        else
        {
            usuario.Desativar(agora);

            // Desativar precisa cortar o acesso agora, nao quando o refresh
            // token expirar sozinho daqui a duas semanas.
            await _autenticacao.RevogarAcessoDoUsuarioAsync(usuario.Id, cancellationToken);
            await AuditarAsync(OperacaoAuditada.UsuarioDesativado, usuario, null, agora, cancellationToken);
        }

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return usuario;
    }

    public async Task<Usuario> DefinirSenhaAsync(
        Guid usuarioId,
        string? novaSenha,
        CancellationToken cancellationToken)
    {
        var agora = _relogio.Agora;

        ValidarSenha(novaSenha);

        var usuario = await ObterAsync(usuarioId, cancellationToken);
        usuario.DefinirSenha(_hashDeSenha.Gerar(novaSenha!), agora);

        await _autenticacao.RevogarAcessoDoUsuarioAsync(usuario.Id, cancellationToken);

        await AuditarAsync(
            OperacaoAuditada.SenhaDeUsuarioAlterada,
            usuario,
            "Senha redefinida por administrador.",
            agora,
            cancellationToken);

        await _unidadeDeTrabalho.SalvarAsync(cancellationToken);

        return usuario;
    }

    private static void ValidarSenha(string? senha)
    {
        if (string.IsNullOrEmpty(senha) ||
            senha.Length < TamanhoMinimoDaSenha ||
            senha.Length > TamanhoMaximoDaSenha)
        {
            throw new ErroDeValidacao(
                "senha",
                $"Senha deve ter entre {TamanhoMinimoDaSenha} e {TamanhoMaximoDaSenha} caracteres.");
        }
    }

    private Task AuditarAsync(
        OperacaoAuditada operacao,
        Usuario alvo,
        string? detalhe,
        DateTimeOffset agora,
        CancellationToken cancellationToken) =>
        _auditoria.RegistrarAsync(
            RegistroDeAuditoria.Registrar(
                _contexto.OrganizacaoId,
                operacao,
                entidade: nameof(Usuario),
                agora,
                autorId: _contexto.UsuarioId,
                autorDescricao: _contexto.UsuarioId.ToString(),
                entidadeId: alvo.Id.ToString(),
                detalhe: detalhe,
                idDeCorrelacao: _correlacao.IdDeCorrelacao),
            cancellationToken);
}
