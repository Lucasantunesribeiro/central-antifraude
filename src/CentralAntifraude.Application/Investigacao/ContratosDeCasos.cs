using CentralAntifraude.Application.Comum;
using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Application.Investigacao;

/// <summary>
/// O que a lista de casos aceita como filtro.
///
/// Mesmo criterio da fila de alertas: vocabulario fechado resolvido pelos
/// **nomes** do enum, e recusa explicita em vez de descarte silencioso. Um
/// filtro ignorado devolveria a lista inteira e o supervisor concluiria que
/// ninguem tem caso em aberto.
/// </summary>
public sealed record FiltroDeCasos
{
    private FiltroDeCasos(
        StatusDoCaso? status,
        ResultadoDaInvestigacao? resultado,
        Guid? responsavelId,
        bool semResponsavel)
    {
        Status = status;
        Resultado = resultado;
        ResponsavelId = responsavelId;
        SemResponsavel = semResponsavel;
    }

    public static FiltroDeCasos Nenhum { get; } = new(null, null, null, false);

    public StatusDoCaso? Status { get; }

    public ResultadoDaInvestigacao? Resultado { get; }

    /// <summary>Casos de uma pessoa. A tela usa isto para "meus casos".</summary>
    public Guid? ResponsavelId { get; }

    /// <summary>Casos que ninguem assumiu — o que a operacao precisa distribuir.</summary>
    public bool SemResponsavel { get; }

    public bool EstaVazio =>
        Status is null && Resultado is null && ResponsavelId is null && !SemResponsavel;

    public static bool TentarCriar(
        string? status,
        string? resultado,
        Guid? responsavelId,
        bool semResponsavel,
        out FiltroDeCasos filtro,
        out string erro)
    {
        filtro = Nenhum;

        if (!VocabularioFechado.TentarResolver<StatusDoCaso>(status, "status", out var statusResolvido, out erro))
        {
            return false;
        }

        if (!VocabularioFechado.TentarResolver<ResultadoDaInvestigacao>(
                resultado,
                "resultado",
                out var resultadoResolvido,
                out erro))
        {
            return false;
        }

        if (responsavelId is not null && semResponsavel)
        {
            erro = "Nao da para pedir os casos de uma pessoa e os casos sem responsavel ao mesmo tempo.";
            return false;
        }

        filtro = new FiltroDeCasos(statusResolvido, resultadoResolvido, responsavelId, semResponsavel);
        erro = string.Empty;

        return true;
    }
}

/// <summary>Um caso na listagem, com o que a tela mostra sem abrir.</summary>
public sealed record CasoNaLista(
    Caso Caso,
    string? ResponsavelNome,
    int QuantidadeDeAlertas,
    int MaiorScore,
    PrioridadeDeAlerta? MaiorPrioridade);

/// <summary>Um alerta do caso com o contexto que o analista precisa ver.</summary>
public sealed record AlertaDoCaso(
    Alerta Alerta,
    Transacao? Transacao,
    IReadOnlyList<SinalDeRisco> Sinais);

/// <summary>
/// O workspace da investigacao.
///
/// <see cref="AcoesPermitidas"/> vem do **servidor**, e nao da tela. O
/// `CLAUDE.md` secao 52 e claro: esconder botao nao e autorizacao. A tela
/// mostra o que o servidor disser que e possivel, e o servidor recusa de novo
/// quando a acao chega — as duas coisas, e nao uma no lugar da outra.
/// </summary>
public sealed record CasoCompleto(
    Caso Caso,
    string? ResponsavelNome,
    string? AbertoPorNome,
    string? ResolvidoPorNome,
    IReadOnlyList<AlertaDoCaso> Alertas,
    IReadOnlyList<EventoDoCaso> Timeline,
    IReadOnlyList<NotaDoCaso> Notas,
    IReadOnlySet<string> AcoesPermitidas);

/// <summary>Acoes que o workspace pode oferecer. Lista fechada.</summary>
public static class AcoesDoCaso
{
    public const string Assumir = "assumir";
    public const string Transferir = "transferir";
    public const string AssociarAlerta = "associarAlerta";
    public const string AdicionarNota = "adicionarNota";
    public const string Resolver = "resolver";
}

/// <summary>Acesso aos casos, notas, timeline e vereditos.</summary>
public interface IRepositorioDeCasos
{
    Task<Pagina<Caso>> ListarAsync(
        FiltroDeCasos filtro,
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken);

    /// <summary>Busca o caso. Nulo para id de outro tenant.</summary>
    Task<Caso?> BuscarPorIdAsync(Guid casoId, CancellationToken cancellationToken);

    /// <summary>A timeline, em ordem de acontecimento.</summary>
    Task<IReadOnlyList<EventoDoCaso>> ListarTimelineAsync(
        Guid casoId,
        CancellationToken cancellationToken);

    /// <summary>As notas, da mais antiga para a mais recente.</summary>
    Task<IReadOnlyList<NotaDoCaso>> ListarNotasAsync(Guid casoId, CancellationToken cancellationToken);

    /// <summary>Alertas de um caso.</summary>
    Task<IReadOnlyList<Alerta>> ListarAlertasDoCasoAsync(
        Guid casoId,
        CancellationToken cancellationToken);

    /// <summary>Resumo de alertas por caso, para a listagem nao virar N+1.</summary>
    Task<IReadOnlyDictionary<Guid, ResumoDosAlertas>> ResumirAlertasAsync(
        IReadOnlyCollection<Guid> casosIds,
        CancellationToken cancellationToken);

    void Adicionar(Caso caso);

    /// <summary>
    /// Grava as entradas de timeline e as notas que a operacao acabou de
    /// produzir.
    ///
    /// Explicito de proposito. Deixar o EF descobri-las por navegacao produzia
    /// um defeito silencioso: com a chave gerada pelo dominio, ele concluia que
    /// a linha ja existia e emitia <c>UPDATE</c> em vez de <c>INSERT</c> — e o
    /// <c>UPDATE</c> de zero linhas virava um falso conflito de concorrencia.
    /// </summary>
    void RegistrarNovidades(Caso caso);

    void AdicionarVeredictos(IReadOnlyCollection<ResultadoDeInvestigacaoDaTransacao> veredictos);
}

/// <summary>Quantos alertas o caso tem e o quanto eles pesam.</summary>
public sealed record ResumoDosAlertas(int Quantidade, int MaiorScore, PrioridadeDeAlerta? MaiorPrioridade);
