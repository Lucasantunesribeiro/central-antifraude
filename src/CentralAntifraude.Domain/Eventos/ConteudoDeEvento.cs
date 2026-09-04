using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.Domain.Eventos;

/// <summary>
/// O que um evento carrega, como contrato tipado e versionado.
///
/// CLAUDE.md secao 41: nao existe evento anonimo nem sem versao. O nome do
/// tipo termina em <c>.v1</c> e faz parte do contrato — uma mudanca
/// incompativel cria <c>.v2</c>, e o consumidor antigo continua entendendo o
/// que ja sabia ler.
///
/// O conteudo e minimo de proposito. Um evento nao e um espelho da linha do
/// banco: ele carrega o que um consumidor precisa para agir, e nada alem
/// (CLAUDE.md secao 56). Fingerprint de IP, referencia de instrumento e
/// fingerprint de dispositivo NAO entram — quem precisar deles vai a
/// transacao, dentro do tenant, com autorizacao.
/// </summary>
public abstract record ConteudoDeEvento
{
    /// <summary>Nome versionado do tipo, como aparece no envelope.</summary>
    public abstract string Tipo { get; }
}

/// <summary>Uma contribuicao de risco, reduzida ao que um consumidor precisa.</summary>
public sealed record SinalDoEvento(string Tipo, int Pontos);

/// <summary>
/// Uma transacao foi avaliada.
///
/// Este e o evento que a Fase 6 vai consumir para criar alertas. Ele carrega
/// a decisao e os sinais para que o consumidor nao precise reavaliar nada —
/// reavaliar no consumidor produziria um resultado potencialmente diferente
/// do que o integrador ja recebeu na resposta sincrona.
/// </summary>
public sealed record TransacaoAvaliadaV1(
    Guid TransacaoId,
    string IdentificadorExterno,
    string ClienteExternoId,
    decimal Valor,
    string Moeda,
    DateTimeOffset OcorridaEm,
    DateTimeOffset RecebidaEm,
    Guid AvaliacaoId,
    int Score,
    Decisao Decisao,
    DateTimeOffset AvaliadaEm,
    Guid VersaoDePerfilId,
    int NumeroDaVersaoDePerfil,
    IReadOnlyList<SinalDoEvento> Sinais) : ConteudoDeEvento
{
    public const string NomeDoTipo = "TransacaoAvaliada.v1";

    public override string Tipo => NomeDoTipo;

    /// <summary>Monta o conteudo a partir da transacao e da avaliacao gravadas.</summary>
    public static TransacaoAvaliadaV1 De(Transacoes.Transacao transacao, AvaliacaoDeRisco avaliacao)
    {
        ArgumentNullException.ThrowIfNull(transacao);
        ArgumentNullException.ThrowIfNull(avaliacao);

        if (transacao.Id != avaliacao.TransacaoId)
        {
            throw new ViolacaoDeInvariante(
                "O evento nao pode juntar uma transacao e uma avaliacao de transacoes diferentes.");
        }

        return new TransacaoAvaliadaV1(
            transacao.Id,
            transacao.IdentificadorExterno,
            transacao.ClienteExternoId,
            transacao.Valor.Valor,
            transacao.Valor.Moeda,
            transacao.OcorridaEm,
            transacao.RecebidaEm,
            avaliacao.Id,
            avaliacao.Score,
            avaliacao.Decisao,
            avaliacao.AvaliadaEm,
            avaliacao.VersaoDePerfilId,
            avaliacao.NumeroDaVersaoDePerfil,
            // Mesma ordem estavel da avaliacao: maior peso primeiro, depois
            // pelo tipo. Dois consumidores comparando dois eventos precisam
            // ver a mesma ordem.
            [.. avaliacao.Sinais
                .OrderByDescending(s => s.Pontos)
                .ThenBy(s => s.Tipo)
                .Select(s => new SinalDoEvento(s.Tipo.ToString(), s.Pontos))]);
    }
}
