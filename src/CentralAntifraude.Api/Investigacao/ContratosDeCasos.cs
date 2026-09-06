using CentralAntifraude.Api.Alertas;
using CentralAntifraude.Application.Investigacao;
using CentralAntifraude.Domain.Investigacao;

namespace CentralAntifraude.Api.Investigacao;

// ---------------------------------------------------------------------------
// Entrada da investigacao.
//
// **O que os DTOs abaixo NAO tem e a defesa contra mass assignment.** Nao ha
// campo `status`, `resultado` em abertura, `responsavelId` direto, `versao`
// gravavel nem `organizacaoId`. Cada um desses so muda pelo metodo de dominio
// que valida a transicao — e o JSON estrito da aplicacao recusa campo
// desconhecido, entao mandar `"status": "Resolvido"` num corpo nao e ignorado
// em silencio: e recusado com 400 (`CLAUDE.md` secoes 53 e 54).
//
// `Versao` aparece em toda alteracao, e nao e mass assignment: ela nunca e
// atribuida, so comparada. E o cliente dizendo "eu estava vendo o estado N" —
// sem isso nao ha como recusar uma gravacao baseada em informacao velha.
// ---------------------------------------------------------------------------

/// <summary>Abre um caso a partir de alertas.</summary>
public sealed record AbrirCasoRequisicao(string Titulo, IReadOnlyList<Guid> AlertasIds);

/// <summary>Traz mais um alerta para o caso.</summary>
public sealed record AssociarAlertaRequisicao(Guid AlertaId, int Versao);

/// <summary>Assume o caso para si.</summary>
public sealed record AssumirCasoRequisicao(int Versao);

/// <summary>Passa o caso para outra pessoa.</summary>
public sealed record TransferirCasoRequisicao(Guid ParaUsuarioId, int Versao);

/// <summary>Acrescenta uma nota de investigacao.</summary>
public sealed record NotaRequisicao(string Conteudo, int Versao);

/// <summary>Conclui a investigacao.</summary>
public sealed record ResolverCasoRequisicao(string Resultado, int Versao);

// ---------------------------------------------------------------------------
// Saida.
// ---------------------------------------------------------------------------

/// <summary>Um caso na listagem.</summary>
public sealed record CasoResumido(
    Guid Id,
    string Titulo,
    string Status,
    string? Resultado,
    Guid? ResponsavelId,
    string? ResponsavelNome,
    int QuantidadeDeAlertas,
    int MaiorScore,
    string? MaiorPrioridade,
    DateTimeOffset AbertoEm,
    DateTimeOffset AtualizadoEm,
    int Versao)
{
    public static CasoResumido De(CasoNaLista item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var caso = item.Caso;

        return new CasoResumido(
            caso.Id,
            caso.Titulo,
            caso.Status.ToString(),
            caso.Resultado?.ToString(),
            caso.ResponsavelId,
            item.ResponsavelNome,
            item.QuantidadeDeAlertas,
            item.MaiorScore,
            item.MaiorPrioridade?.ToString(),
            caso.AbertoEm,
            caso.AtualizadoEm,
            caso.Versao);
    }
}

/// <summary>Uma entrada da timeline.</summary>
public sealed record EventoDoCasoResposta(
    Guid Id,
    string Tipo,
    string Descricao,
    Guid AutorId,
    string AutorNome,
    Guid? ReferenciaId,
    DateTimeOffset OcorridoEm)
{
    public static EventoDoCasoResposta De(EventoDoCaso evento)
    {
        ArgumentNullException.ThrowIfNull(evento);

        return new EventoDoCasoResposta(
            evento.Id,
            evento.Tipo.ToString(),
            evento.Descricao,
            evento.AutorId,
            evento.AutorDescricao,
            evento.ReferenciaId,
            evento.OcorridoEm);
    }
}

/// <summary>Uma nota de investigacao, em texto puro.</summary>
public sealed record NotaResposta(
    Guid Id,
    string Conteudo,
    Guid AutorId,
    string AutorNome,
    DateTimeOffset CriadaEm)
{
    public static NotaResposta De(NotaDoCaso nota)
    {
        ArgumentNullException.ThrowIfNull(nota);

        // O conteudo vai **exatamente** como foi gravado. Nada e escapado nem
        // limpo aqui: a tela renderiza texto, e escapar no meio do caminho
        // faria a nota exibida diferir da nota escrita.
        return new NotaResposta(
            nota.Id,
            nota.Conteudo,
            nota.AutorId,
            nota.AutorDescricao,
            nota.CriadaEm);
    }
}

/// <summary>Um alerta do caso, com a transacao e os sinais.</summary>
public sealed record AlertaDoCasoResposta(
    Guid Id,
    Guid TransacaoId,
    string Decisao,
    int Score,
    string Prioridade,
    string Status,
    DateTimeOffset CriadoEm,
    string? IdentificadorExterno,
    decimal? Valor,
    string? Moeda,
    string? ClienteExternoId,
    DateTimeOffset? OcorridaEm,
    IReadOnlyList<SinalResumido> Sinais)
{
    public static AlertaDoCasoResposta De(AlertaDoCaso item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var alerta = item.Alerta;
        var transacao = item.Transacao;

        return new AlertaDoCasoResposta(
            alerta.Id,
            alerta.TransacaoId,
            alerta.Decisao.ToString(),
            alerta.Score,
            alerta.Prioridade.ToString(),
            alerta.Status.ToString(),
            alerta.CriadoEm,
            transacao?.IdentificadorExterno,
            transacao?.Valor.Valor,
            transacao?.Valor.Moeda,
            transacao?.ClienteExternoId,
            transacao?.OcorridaEm,
            // Todos os sinais, e nao so os tres principais: aqui o analista
            // esta investigando, e nao varrendo uma fila.
            [.. item.Sinais.Select(s => new SinalResumido(s.Tipo.ToString(), s.Pontos))]);
    }
}

/// <summary>
/// O workspace da investigacao.
///
/// <c>AcoesPermitidas</c> vem do servidor. A tela mostra o que o servidor
/// disser que e possivel, e o servidor recusa de novo quando a acao chega —
/// as duas coisas, e nao uma no lugar da outra (`CLAUDE.md` secao 52).
/// </summary>
public sealed record CasoDetalhado(
    Guid Id,
    string Titulo,
    string Status,
    string? Resultado,
    Guid? ResponsavelId,
    string? ResponsavelNome,
    string? AbertoPorNome,
    string? ResolvidoPorNome,
    DateTimeOffset AbertoEm,
    DateTimeOffset AtualizadoEm,
    DateTimeOffset? ResolvidoEm,
    int Versao,
    IReadOnlyList<AlertaDoCasoResposta> Alertas,
    IReadOnlyList<EventoDoCasoResposta> Timeline,
    IReadOnlyList<NotaResposta> Notas,
    IReadOnlyList<string> AcoesPermitidas)
{
    public static CasoDetalhado De(CasoCompleto completo)
    {
        ArgumentNullException.ThrowIfNull(completo);

        var caso = completo.Caso;

        return new CasoDetalhado(
            caso.Id,
            caso.Titulo,
            caso.Status.ToString(),
            caso.Resultado?.ToString(),
            caso.ResponsavelId,
            completo.ResponsavelNome,
            completo.AbertoPorNome,
            completo.ResolvidoPorNome,
            caso.AbertoEm,
            caso.AtualizadoEm,
            caso.ResolvidoEm,
            caso.Versao,
            [.. completo.Alertas.Select(AlertaDoCasoResposta.De)],
            // Ja vem em ordem cronologica do repositorio: a timeline e uma
            // historia, e historia se le do comeco.
            [.. completo.Timeline.Select(EventoDoCasoResposta.De)],
            [.. completo.Notas.Select(NotaResposta.De)],
            [.. completo.AcoesPermitidas.Order(StringComparer.Ordinal)]);
    }
}
