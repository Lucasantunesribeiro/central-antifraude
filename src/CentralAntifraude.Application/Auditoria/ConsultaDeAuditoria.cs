using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Identidade;

namespace CentralAntifraude.Application.Auditoria;

/// <summary>
/// O que a consulta de auditoria aceita como filtro.
///
/// **Nao ha filtro por tipo de entidade**, e a ausencia e deliberada. A
/// operacao ja diz sobre o que ela foi — `VersaoDeRegraPublicada` so acontece
/// numa regra — e um segundo vocabulario com os nomes das entidades sairia de
/// sincronia na primeira entidade nova, silenciosamente: o filtro deixaria de
/// encontrar registros que existem.
///
/// <see cref="EntidadeId"/> cobre a pergunta que realmente se faz numa
/// investigacao: "tudo o que aconteceu com este caso".
/// </summary>
public sealed record FiltroDeAuditoria
{
    private FiltroDeAuditoria(
        OperacaoAuditada? operacao,
        Guid? autorId,
        Guid? entidadeId,
        DateTimeOffset? de,
        DateTimeOffset? ate)
    {
        Operacao = operacao;
        AutorId = autorId;
        EntidadeId = entidadeId;
        De = de;
        Ate = ate;
    }

    public static FiltroDeAuditoria Nenhum { get; } = new(null, null, null, null, null);

    public OperacaoAuditada? Operacao { get; }

    public Guid? AutorId { get; }

    /// <summary>Identificador do recurso afetado, gravado como texto na trilha.</summary>
    public Guid? EntidadeId { get; }

    public DateTimeOffset? De { get; }

    public DateTimeOffset? Ate { get; }

    public bool EstaVazio =>
        Operacao is null && AutorId is null && EntidadeId is null && De is null && Ate is null;

    public static bool TentarCriar(
        string? operacao,
        Guid? autorId,
        Guid? entidadeId,
        DateTimeOffset? de,
        DateTimeOffset? ate,
        out FiltroDeAuditoria filtro,
        out string erro)
    {
        filtro = Nenhum;

        if (!VocabularioFechado.TentarResolver<OperacaoAuditada>(
                operacao,
                "operacao",
                out var operacaoResolvida,
                out erro))
        {
            return false;
        }

        if (de is not null && ate is not null && de > ate)
        {
            erro = "O inicio do periodo nao pode ser depois do fim.";
            return false;
        }

        filtro = new FiltroDeAuditoria(operacaoResolvida, autorId, entidadeId, de, ate);

        return true;
    }
}

/// <summary>
/// Leitura da trilha.
///
/// Interface separada de <see cref="IRegistradorDeAuditoria"/> de proposito.
/// Aquela so escreve, e continuar assim deixa obvio que nada no caminho de
/// gravacao pode ler, alterar ou apagar o que ja foi registrado — a trilha e
/// somente insercao (CLAUDE.md secao 67).
/// </summary>
public interface IConsultaDeAuditoria
{
    Task<Pagina<RegistroDeAuditoria>> ListarAsync(
        FiltroDeAuditoria filtro,
        ParametrosDePaginacao paginacao,
        CancellationToken cancellationToken);

    /// <summary>Operacoes que de fato aparecem na trilha do tenant, para montar o filtro.</summary>
    Task<IReadOnlyList<OperacaoAuditada>> ListarOperacoesUsadasAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// A consulta da trilha de auditoria.
///
/// **Quem le e quem audita.** Administrador e Auditor alcancam a trilha;
/// Supervisor e Analista, nao. A trilha e um controle **sobre** o que eles
/// fazem — publicar regra, resolver caso —, e dar a quem e auditado o poder de
/// varrer o proprio rastro enfraquece o unico registro que responde "quem fez
/// o que e quando" (CLAUDE.md secoes 8.3 e 67).
///
/// **O que a trilha guarda ja e o que pode ser exibido.** Ela nunca recebeu
/// senha, hash, token nem payload completo — a decisao e da Fase 1, e esta
/// consulta nao precisa filtrar nada na saida. O campo <c>Detalhe</c> carrega
/// um resumo curto e seguro, do tipo "Perfil: Auditor -&gt; Analista": e o
/// "antes/depois" que o ROADMAP 10.6 pede, na forma que nao vaza.
/// </summary>
public sealed class ServicoDeAuditoria
{
    private readonly IConsultaDeAuditoria _consulta;
    private readonly IContextoDoUsuarioAtual _contextoAtual;

    public ServicoDeAuditoria(IConsultaDeAuditoria consulta, IContextoDoUsuarioAtual contextoAtual)
    {
        _consulta = consulta;
        _contextoAtual = contextoAtual;
    }

    public async Task<Pagina<RegistroDeAuditoria>> ListarAsync(
        FiltroDeAuditoria filtro,
        ParametrosDePaginacao paginacao,
        CancellationToken cancellationToken)
    {
        GarantirLeituraDeAuditoria();

        return await _consulta.ListarAsync(filtro, paginacao, cancellationToken);
    }

    public async Task<IReadOnlyList<OperacaoAuditada>> ListarOperacoesUsadasAsync(
        CancellationToken cancellationToken)
    {
        GarantirLeituraDeAuditoria();

        return await _consulta.ListarOperacoesUsadasAsync(cancellationToken);
    }

    private void GarantirLeituraDeAuditoria()
    {
        // A politica da rota ja recusa antes daqui; esta e a segunda camada,
        // para o caso de uma rota futura esquecer a politica.
        if (_contextoAtual.Perfil is not (PerfilDeUsuario.Administrador or PerfilDeUsuario.Auditor))
        {
            throw new NaoAutorizado(
                "A trilha de auditoria e consultada por Administrador e Auditor.");
        }
    }
}
