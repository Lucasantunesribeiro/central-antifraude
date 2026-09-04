using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Identidade;

/// <summary>
/// Usuario humano da Central Antifraude.
///
/// Pertence a exatamente uma organizacao, com exatamente um perfil.
/// Integracoes maquina-a-maquina NAO sao usuarios (CLAUDE.md secao 50) —
/// elas ganham credencial propria na Fase 2.
/// </summary>
public sealed class Usuario
{
    public const int TamanhoMaximoDoNome = 200;

    private Usuario(
        Guid id,
        Guid organizacaoId,
        Email email,
        string nomeCompleto,
        string hashDaSenha,
        PerfilDeUsuario perfil,
        DateTimeOffset agora)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        Email = email;
        NomeCompleto = nomeCompleto;
        HashDaSenha = hashDaSenha;
        Perfil = perfil;
        Ativo = true;
        CriadoEm = agora;
        AtualizadoEm = agora;
    }

    // Construtor usado pelo EF Core na materializacao.
    private Usuario()
    {
        NomeCompleto = string.Empty;
        HashDaSenha = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// O tenant a que este usuario pertence. Nunca vem do payload de uma
    /// requisicao: e a origem da autoridade, nao um dado informado.
    /// </summary>
    public Guid OrganizacaoId { get; private set; }

    /// <summary>Chave unica global de identidade. Ver docs/adr/0005.</summary>
    public Email Email { get; private set; }

    public string NomeCompleto { get; private set; }

    /// <summary>
    /// Hash da senha, nunca a senha. Formato e parametros ficam a cargo da
    /// implementacao de hash (Infrastructure) — o dominio so guarda a string
    /// opaca que ela produz.
    /// </summary>
    public string HashDaSenha { get; private set; }

    public PerfilDeUsuario Perfil { get; private set; }

    /// <summary>
    /// Usuario inativo nao autentica e tem as sessoes abertas revogadas.
    /// Desativar e reversivel; apagar registro ligado a trilha de auditoria
    /// nao seria.
    /// </summary>
    public bool Ativo { get; private set; }

    public DateTimeOffset CriadoEm { get; private set; }

    public DateTimeOffset AtualizadoEm { get; private set; }

    public static Usuario Criar(
        Guid organizacaoId,
        Email email,
        string nomeCompleto,
        string hashDaSenha,
        PerfilDeUsuario perfil,
        DateTimeOffset agora)
    {
        if (organizacaoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Usuario precisa pertencer a uma organizacao.");
        }

        if (!email.EhValido)
        {
            throw new ViolacaoDeInvariante("E-mail do usuario e invalido.");
        }

        if (string.IsNullOrWhiteSpace(nomeCompleto) || nomeCompleto.Trim().Length > TamanhoMaximoDoNome)
        {
            throw new ViolacaoDeInvariante(
                $"Nome e obrigatorio e deve ter no maximo {TamanhoMaximoDoNome} caracteres.");
        }

        if (string.IsNullOrWhiteSpace(hashDaSenha))
        {
            throw new ViolacaoDeInvariante("Usuario precisa de uma senha definida.");
        }

        if (!Enum.IsDefined(perfil))
        {
            throw new ViolacaoDeInvariante("Perfil de usuario desconhecido.");
        }

        return new Usuario(
            Identificador.Novo(),
            organizacaoId,
            email,
            nomeCompleto.Trim(),
            hashDaSenha,
            perfil,
            agora);
    }

    public void AlterarPerfil(PerfilDeUsuario novoPerfil, DateTimeOffset agora)
    {
        if (!Enum.IsDefined(novoPerfil))
        {
            throw new ViolacaoDeInvariante("Perfil de usuario desconhecido.");
        }

        if (Perfil == novoPerfil)
        {
            return;
        }

        Perfil = novoPerfil;
        AtualizadoEm = agora;
    }

    public void AlterarNome(string novoNome, DateTimeOffset agora)
    {
        if (string.IsNullOrWhiteSpace(novoNome) || novoNome.Trim().Length > TamanhoMaximoDoNome)
        {
            throw new ViolacaoDeInvariante(
                $"Nome e obrigatorio e deve ter no maximo {TamanhoMaximoDoNome} caracteres.");
        }

        NomeCompleto = novoNome.Trim();
        AtualizadoEm = agora;
    }

    public void DefinirSenha(string novoHash, DateTimeOffset agora)
    {
        if (string.IsNullOrWhiteSpace(novoHash))
        {
            throw new ViolacaoDeInvariante("Hash de senha nao pode ser vazio.");
        }

        HashDaSenha = novoHash;
        AtualizadoEm = agora;
    }

    public void Desativar(DateTimeOffset agora)
    {
        if (!Ativo)
        {
            return;
        }

        Ativo = false;
        AtualizadoEm = agora;
    }

    public void Reativar(DateTimeOffset agora)
    {
        if (Ativo)
        {
            return;
        }

        Ativo = true;
        AtualizadoEm = agora;
    }
}
