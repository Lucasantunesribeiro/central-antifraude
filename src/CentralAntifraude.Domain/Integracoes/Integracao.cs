using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Integracoes;

/// <summary>
/// Um sistema externo autorizado a enviar transacoes para a Central
/// Antifraude.
///
/// Integracao NAO e usuario (CLAUDE.md secao 50): ela tem credencial propria,
/// nao faz login, nao tem perfil RBAC e nao aparece em nenhuma tela de
/// pessoas. Confundir os dois seria dar a um sistema automatizado a mesma
/// superficie de uma sessao humana.
///
/// O tenant da integracao e a UNICA fonte de autoridade sobre a organizacao
/// das transacoes que ela envia. O payload nunca escolhe o tenant.
/// </summary>
public sealed class Integracao
{
    public const int TamanhoMaximoDoNome = 120;

    private Integracao(Guid id, Guid organizacaoId, string nome, DateTimeOffset agora)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        Nome = nome;
        Ativa = true;
        CriadaEm = agora;
        AtualizadaEm = agora;
    }

    // Construtor usado pelo EF Core na materializacao.
    private Integracao() => Nome = string.Empty;

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    /// <summary>Nome de exibicao. Ex.: "Checkout web", "App iOS".</summary>
    public string Nome { get; private set; }

    /// <summary>
    /// Integracao inativa nao autentica, mesmo com credencial valida em maos.
    /// Desativar e reversivel; apagar o historico de transacoes nao seria.
    /// </summary>
    public bool Ativa { get; private set; }

    public DateTimeOffset CriadaEm { get; private set; }

    public DateTimeOffset AtualizadaEm { get; private set; }

    public static Integracao Criar(Guid organizacaoId, string nome, DateTimeOffset agora)
    {
        if (organizacaoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Integracao precisa pertencer a uma organizacao.");
        }

        if (string.IsNullOrWhiteSpace(nome) || nome.Trim().Length > TamanhoMaximoDoNome)
        {
            throw new ViolacaoDeInvariante(
                $"Nome da integracao e obrigatorio e deve ter no maximo {TamanhoMaximoDoNome} caracteres.");
        }

        return new Integracao(Identificador.Novo(), organizacaoId, nome.Trim(), agora);
    }

    public void Renomear(string novoNome, DateTimeOffset agora)
    {
        if (string.IsNullOrWhiteSpace(novoNome) || novoNome.Trim().Length > TamanhoMaximoDoNome)
        {
            throw new ViolacaoDeInvariante(
                $"Nome da integracao e obrigatorio e deve ter no maximo {TamanhoMaximoDoNome} caracteres.");
        }

        Nome = novoNome.Trim();
        AtualizadaEm = agora;
    }

    public void Desativar(DateTimeOffset agora)
    {
        if (!Ativa)
        {
            return;
        }

        Ativa = false;
        AtualizadaEm = agora;
    }

    public void Reativar(DateTimeOffset agora)
    {
        if (Ativa)
        {
            return;
        }

        Ativa = true;
        AtualizadaEm = agora;
    }
}
