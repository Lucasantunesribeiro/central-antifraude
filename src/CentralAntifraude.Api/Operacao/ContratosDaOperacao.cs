using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Operacao;
using CentralAntifraude.Domain.Auditoria;

namespace CentralAntifraude.Api.Operacao;

/// <summary>Uma faixa de decisao e quantas avaliacoes cairam nela.</summary>
public sealed record ContagemResposta(string Chave, int Quantidade);

/// <summary>Um dia da serie temporal.</summary>
public sealed record DiaResposta(string Dia, int Permitir, int Revisar, int Bloquear, int Total);

/// <summary>Um tipo de sinal e quantas vezes acionou.</summary>
public sealed record SinalFrequenteResposta(string Tipo, int Acionamentos);

/// <summary>
/// O painel operacional.
///
/// <c>transacoesRecebidas</c> e <c>transacoesAvaliadas</c> sao numeros
/// diferentes de propósito, e a tela diz por que: uma transacao atrasada chega
/// hoje sobre um fato de ontem, e uma que chegou ontem as 23h59 pode ter sido
/// decidida hoje. Igualar os dois esconderia exatamente o comportamento que o
/// produto existe para tratar.
/// </summary>
public sealed record PainelResposta(
    int Dias,
    DateTimeOffset Inicio,
    DateTimeOffset Fim,
    int TransacoesRecebidas,
    int TransacoesAvaliadas,
    IReadOnlyList<ContagemResposta> Decisoes,
    IReadOnlyList<DiaResposta> Tendencia,
    IReadOnlyList<SinalFrequenteResposta> SinaisMaisFrequentes,
    int AlertasAbertos,
    IReadOnlyList<ContagemResposta> Casos,
    int CasosAntigos,
    int DiasParaCasoAntigo,
    int EventosPendentes)
{
    public static PainelResposta De(ResumoOperacional resumo)
    {
        ArgumentNullException.ThrowIfNull(resumo);

        return new PainelResposta(
            resumo.Janela.Dias,
            resumo.Janela.De,
            resumo.Janela.Ate,
            resumo.TransacoesRecebidas,
            resumo.TransacoesAvaliadas,
            [.. resumo.Decisoes.Select(d => new ContagemResposta(d.Decisao.ToString(), d.Quantidade))],
            [
                .. resumo.Tendencia.Select(d => new DiaResposta(
                    d.Dia.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    d.Permitir,
                    d.Revisar,
                    d.Bloquear,
                    d.Total)),
            ],
            [
                .. resumo.SinaisMaisFrequentes.Select(s => new SinalFrequenteResposta(
                    s.Tipo.ToString(),
                    s.Acionamentos)),
            ],
            resumo.AlertasAbertos,
            [.. resumo.Casos.Select(c => new ContagemResposta(c.Status.ToString(), c.Quantidade))],
            resumo.CasosAntigos,
            ServicoDoPainel.DiasParaCasoAntigo,
            resumo.EventosPendentes);
    }
}

/// <summary>
/// Acionamentos de uma regra no periodo, com o que a investigacao apurou.
///
/// **Sao contagens, e nao nota de qualidade** (ROADMAP 10.5). As transacoes
/// com veredito nao sao amostra aleatoria: elas viraram caso porque o motor as
/// marcou. O contrato nao expoe nenhuma taxa calculada — quem le tem os
/// numeros brutos e o denominador.
/// </summary>
public sealed record MetricaDeRegraResposta(
    Guid RegraId,
    string Nome,
    string Tipo,
    int Acionamentos,
    int FraudeConfirmada,
    int Legitima,
    int Inconclusiva,
    int SemResultadoConhecido)
{
    public static MetricaDeRegraResposta De(MetricaDeRegra metrica)
    {
        ArgumentNullException.ThrowIfNull(metrica);

        return new MetricaDeRegraResposta(
            metrica.RegraId,
            metrica.Nome,
            metrica.Tipo.ToString(),
            metrica.Acionamentos,
            metrica.FraudeConfirmada,
            metrica.Legitima,
            metrica.Inconclusiva,
            metrica.SemResultadoConhecido);
    }
}

/// <summary>
/// Uma linha da trilha de auditoria.
///
/// **O que a trilha guarda ja e o que pode ser exibido.** Ela nunca recebeu
/// senha, hash, token nem payload completo — a decisao e da Fase 1, e esta
/// resposta nao precisa filtrar nada. <c>detalhe</c> carrega o resumo curto e
/// seguro do tipo "Perfil: Auditor -&gt; Analista", que e o "antes/depois" que
/// o ROADMAP 10.6 pede na forma que nao vaza.
/// </summary>
public sealed record RegistroDeAuditoriaResposta(
    Guid Id,
    string Operacao,
    Guid? AutorId,
    string? Autor,
    string Entidade,
    string? EntidadeId,
    string? Detalhe,
    string? IdDeCorrelacao,
    DateTimeOffset OcorridoEm)
{
    public static RegistroDeAuditoriaResposta De(RegistroDeAuditoria registro)
    {
        ArgumentNullException.ThrowIfNull(registro);

        return new RegistroDeAuditoriaResposta(
            registro.Id,
            registro.Operacao.ToString(),
            registro.AutorId,
            registro.AutorDescricao,
            registro.Entidade,
            registro.EntidadeId,
            registro.Detalhe,
            registro.IdDeCorrelacao,
            registro.OcorridoEm);
    }
}

/// <summary>Uma pagina da trilha.</summary>
public sealed record PaginaDeAuditoria(
    IReadOnlyList<RegistroDeAuditoriaResposta> Itens,
    int Pagina,
    int Tamanho,
    long TotalDeItens,
    int TotalDePaginas)
{
    public static PaginaDeAuditoria De(Pagina<RegistroDeAuditoria> pagina)
    {
        ArgumentNullException.ThrowIfNull(pagina);

        return new PaginaDeAuditoria(
            [.. pagina.Itens.Select(RegistroDeAuditoriaResposta.De)],
            pagina.PaginaAtual,
            pagina.TamanhoDaPagina,
            pagina.TotalDeItens,
            pagina.TotalDePaginas);
    }
}
