using CentralAntifraude.Application.Risco;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Api.Risco;

// ---------------------------------------------------------------------------
// Saida do risco.
//
// Nao ha entrada nesta pasta, e isso e deliberado: nenhuma rota HTTP aceita
// score, decisao, sinal ou peso vindos do cliente. O score e produzido pelo
// motor a partir da transacao e do contexto historico, e o unico caminho ate
// o banco passa por `AvaliacaoDeRisco.Registrar`, que e `internal` ao dominio
// (CLAUDE.md secoes 19 e 53).
// ---------------------------------------------------------------------------

/// <summary>
/// Uma evidencia produzida por uma regra.
///
/// Traz a versao exata da regra que a gerou. E o que permite abrir uma
/// avaliacao de meses atras e entender a decisao com a configuracao daquele
/// momento, mesmo que a regra ja tenha sido republicada desde entao
/// (CLAUDE.md secao 17).
/// </summary>
public sealed record SinalResposta(
    string Tipo,
    string Explicacao,
    int Pontos,
    Guid RegraId,
    int VersaoDaRegra,
    IReadOnlyDictionary<string, string> Evidencia)
{
    public static SinalResposta De(SinalDeRisco sinal)
    {
        ArgumentNullException.ThrowIfNull(sinal);

        return new SinalResposta(
            sinal.Tipo.ToString(),
            sinal.Explicacao,
            sinal.Pontos,
            sinal.RegraId,
            sinal.NumeroDaVersaoDeRegra,
            sinal.DadosDaEvidencia);
    }
}

/// <summary>
/// A avaliacao completa de uma transacao.
///
/// <see cref="SomaBrutaDosPontos"/> e <see cref="ScoreFoiLimitado"/> existem
/// para que a tela nao minta: quando as regras somam 110 e o score final e
/// 100, o analista ve que houve teto, em vez de achar que as contribuicoes
/// exibidas somam exatamente o score.
/// </summary>
public sealed record AvaliacaoResposta(
    Guid Id,
    int Score,
    string Decisao,
    DateTimeOffset AvaliadaEm,
    Guid VersaoDePerfilId,
    int NumeroDaVersaoDePerfil,
    string VersaoDoMotor,
    int SomaBrutaDosPontos,
    bool ScoreFoiLimitado,
    IReadOnlyList<SinalResposta> Sinais)
{
    public static AvaliacaoResposta De(AvaliacaoDeRisco avaliacao)
    {
        ArgumentNullException.ThrowIfNull(avaliacao);

        return new AvaliacaoResposta(
            avaliacao.Id,
            avaliacao.Score,
            avaliacao.Decisao.ToString(),
            avaliacao.AvaliadaEm,
            avaliacao.VersaoDePerfilId,
            avaliacao.NumeroDaVersaoDePerfil,
            avaliacao.VersaoDoMotorUsada,
            avaliacao.SomaBrutaDosPontos,
            avaliacao.ScoreFoiLimitado,
            // Ordem estavel: os sinais de maior peso primeiro. A tela abre
            // pelo que mais pesou, e duas consultas da mesma avaliacao
            // devolvem a mesma ordem.
            [.. avaliacao.Sinais
                .OrderByDescending(s => s.Pontos)
                .ThenBy(s => s.Tipo)
                .ThenBy(s => s.RegraId)
                .Select(SinalResposta.De)]);
    }

    public static AvaliacaoResposta? DeOpcional(AvaliacaoDeRisco? avaliacao) =>
        avaliacao is null ? null : De(avaliacao);
}

/// <summary>
/// Transacao na listagem operacional, com o resultado do risco.
///
/// <c>Avaliacao</c> pode vir nula: transacoes registradas antes da Fase 3
/// existem sem avaliacao. A tela mostra "sem avaliacao" — inventar score zero
/// seria apresentar uma decisao que ninguem tomou.
/// </summary>
public sealed record TransacaoAvaliadaResumida(
    Guid Id,
    string IdentificadorExterno,
    decimal Valor,
    string Moeda,
    DateTimeOffset OcorridaEm,
    DateTimeOffset RecebidaEm,
    string ClienteExternoId,
    string? PaisDeOrigem,
    int? Score,
    string? Decisao)
{
    public static TransacaoAvaliadaResumida De(TransacaoAvaliada item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var transacao = item.Transacao;

        return new TransacaoAvaliadaResumida(
            transacao.Id,
            transacao.IdentificadorExterno,
            transacao.Valor.Valor,
            transacao.Valor.Moeda,
            transacao.OcorridaEm,
            transacao.RecebidaEm,
            transacao.ClienteExternoId,
            transacao.PaisDeOrigem,
            item.Avaliacao?.Score,
            item.Avaliacao?.Decisao.ToString());
    }
}

/// <summary>
/// Detalhe da transacao com a avaliacao inteira.
///
/// O fingerprint do dispositivo aparece; o do IP nao. O de dispositivo e
/// fornecido pela integracao e serve para o analista reconhecer o aparelho
/// entre transacoes. O de IP e um HMAC derivado de um endereco que o produto
/// escolheu nao guardar (CLAUDE.md secao 57) — exibi-lo nao ajudaria a
/// investigacao e ampliaria a superficie de dado sensivel na tela.
/// </summary>
public sealed record TransacaoDetalhada(
    Guid Id,
    string IdentificadorExterno,
    decimal Valor,
    string Moeda,
    DateTimeOffset OcorridaEm,
    DateTimeOffset RecebidaEm,
    string ClienteExternoId,
    string ReferenciaDoInstrumento,
    string? FingerprintDoDispositivo,
    string? PaisDeOrigem,
    AvaliacaoResposta? Avaliacao)
{
    public static TransacaoDetalhada De(TransacaoAvaliada item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var transacao = item.Transacao;

        return new TransacaoDetalhada(
            transacao.Id,
            transacao.IdentificadorExterno,
            transacao.Valor.Valor,
            transacao.Valor.Moeda,
            transacao.OcorridaEm,
            transacao.RecebidaEm,
            transacao.ClienteExternoId,
            transacao.ReferenciaDoInstrumento,
            transacao.FingerprintDoDispositivo,
            transacao.PaisDeOrigem,
            AvaliacaoResposta.DeOpcional(item.Avaliacao));
    }
}

/// <summary>
/// Uma regra do catalogo, na versao vigente.
///
/// <c>Configuracao</c> vai como texto legivel, produzido por
/// <c>ConfiguracaoDeRegra.Descrever()</c>, e nao como o JSON cru: a tela
/// precisa dizer "mais de 3 transacoes em 10 minutos", nao despejar a
/// estrutura interna.
/// </summary>
public sealed record RegraResposta(
    Guid Id,
    string Tipo,
    string Nome,
    int VersaoAtual,
    int Pontos,
    string Configuracao,
    DateTimeOffset PublicadaEm)
{
    public static RegraResposta De(Regra regra, VersaoDeRegra versao)
    {
        ArgumentNullException.ThrowIfNull(regra);
        ArgumentNullException.ThrowIfNull(versao);

        return new RegraResposta(
            regra.Id,
            regra.Tipo.ToString(),
            regra.Nome,
            versao.Numero,
            versao.Pontos,
            versao.Configuracao.Descrever(),
            versao.PublicadaEm);
    }
}

/// <summary>Perfil de risco em vigor, com os limiares e as regras que o compoem.</summary>
public sealed record PerfilVigenteResposta(
    Guid VersaoId,
    int Numero,
    int LimiarDeRevisao,
    int LimiarDeBloqueio,
    DateTimeOffset PublicadaEm,
    IReadOnlyList<RegraResposta> Regras);
