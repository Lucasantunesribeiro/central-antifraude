using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Domain.Risco;

/// <summary>
/// O motor de risco.
///
/// **Deterministico** (CLAUDE.md secao 19): mesma transacao, mesmo contexto e
/// mesma versao de perfil produzem sempre o mesmo score, a mesma decisao e os
/// mesmos sinais, na mesma ordem.
///
/// Isso e possivel porque o motor nao tem nada de fora: sem banco, sem
/// relogio proprio, sem aleatoriedade, sem IA. O instante da avaliacao chega
/// por parametro e serve apenas para carimbar o resultado — nenhuma regra o
/// usa para decidir. As janelas de tempo sao ancoradas em <c>OcorridaEm</c> da
/// transacao, e nao em "agora".
///
/// **IA nao participa** (secoes 19 e 64). Nada aqui adiciona ponto, cria sinal
/// ou muda decisao a partir de modelo probabilistico. Se um dia existir IA no
/// produto, ela resume um caso para o analista ler — nunca decide.
/// </summary>
public sealed class MotorDeRisco
{
    private readonly IReadOnlyDictionary<TipoDeRegra, IAvaliadorDeRegra> _avaliadores;

    /// <summary>
    /// Motor com o catalogo completo de avaliadores.
    ///
    /// Registro explicito, sem reflexao: um tipo novo de regra so entra em
    /// producao quando alguem o adiciona aqui, o que torna a lista de regras
    /// legivel em um lugar so.
    /// </summary>
    public MotorDeRisco()
        : this(
        [
            new AvaliadorDeVelocidade(),
            new AvaliadorDeNovoDispositivo(),
            new AvaliadorDeValorAcimaDoHistorico(),
            new AvaliadorDeDivergenciaGeografica(),
        ])
    {
    }

    /// <summary>Construtor usado por testes que exercitam um subconjunto de regras.</summary>
    public MotorDeRisco(IReadOnlyList<IAvaliadorDeRegra> avaliadores)
    {
        ArgumentNullException.ThrowIfNull(avaliadores);

        _avaliadores = avaliadores.ToDictionary(a => a.Tipo);
    }

    /// <summary>Tipos que este motor sabe avaliar.</summary>
    public IReadOnlyCollection<TipoDeRegra> TiposSuportados => _avaliadores.Keys.ToList();

    public AvaliacaoDeRisco Avaliar(
        Transacao transacao,
        VersaoDePerfilDeRisco versaoDoPerfil,
        ContextoDeRisco contexto,
        DateTimeOffset avaliadaEm)
    {
        ArgumentNullException.ThrowIfNull(transacao);
        ArgumentNullException.ThrowIfNull(versaoDoPerfil);
        ArgumentNullException.ThrowIfNull(contexto);

        if (transacao.OrganizacaoId != versaoDoPerfil.OrganizacaoId)
        {
            // Avaliar uma transacao com o perfil de outra organizacao seria
            // vazamento entre tenants no lugar mais silencioso possivel: o
            // resultado sairia plausivel e ninguem notaria.
            throw new ViolacaoDeInvariante(
                "A transacao e a versao do perfil pertencem a organizacoes diferentes.");
        }

        var sinais = new List<(VersaoDeRegra, SinalCalculado)>();

        // Ordem estavel: as versoes de regra sao percorridas ordenadas pelo
        // tipo. Sem isso, dois bancos com a mesma configuracao poderiam
        // devolver os sinais em ordens diferentes - e a comparacao de duas
        // avaliacoes deixaria de ser confiavel.
        foreach (var versaoDeRegra in versaoDoPerfil.VersoesDeRegra.OrderBy(v => v.Tipo))
        {
            if (!_avaliadores.TryGetValue(versaoDeRegra.Tipo, out var avaliador))
            {
                // Versao de regra de um tipo que este motor nao conhece.
                // Ignorar em silencio seria pior do que falhar: a avaliacao
                // sairia com score menor do que o perfil pede, e a explicacao
                // nao mencionaria a ausencia.
                throw new ViolacaoDeInvariante(
                    $"Nao ha avaliador para o tipo de regra {versaoDeRegra.Tipo}. " +
                    "O perfil publicado referencia uma regra que este motor nao sabe executar.");
            }

            if (avaliador.Avaliar(versaoDeRegra.Configuracao, transacao, contexto) is { } calculado)
            {
                sinais.Add((versaoDeRegra, calculado));
            }
        }

        return AvaliacaoDeRisco.Registrar(transacao.Id, versaoDoPerfil, sinais, avaliadaEm);
    }
}
