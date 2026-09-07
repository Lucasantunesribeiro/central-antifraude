using CentralAntifraude.Domain.Backtests;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Infrastructure.Persistencia.Configuracoes;

namespace CentralAntifraude.UnitTests.Infraestrutura;

/// <summary>
/// O snapshot do candidato e o resultado vao e voltam do banco sem perder
/// nada — e sem abrir caminho para executar o que estiver gravado.
///
/// O candidato carrega <see cref="ConfiguracaoDeRegra"/>, que e polimorfica. A
/// unica porta pela qual ela volta do banco continua sendo o <c>switch</c>
/// fechado do serializador de configuracao: deixar o <c>System.Text.Json</c>
/// resolver o tipo sozinho seria abrir exatamente a porta que o catalogo
/// fechado existe para manter trancada (CLAUDE.md secao 21).
/// </summary>
public class SerializadorDoBacktestTests
{
    private static PerfilCandidato Candidato() =>
        new(
            40,
            70,
            [
                new RegraCandidata(
                    Guid.CreateVersion7(),
                    "Velocidade noturna",
                    TipoDeRegra.VelocidadePorCliente,
                    new ConfiguracaoDeVelocidade(2, 30),
                    45,
                    OrigemDaRegraCandidata.Rascunho),
                new RegraCandidata(
                    Guid.CreateVersion7(),
                    "Valor acima do historico",
                    TipoDeRegra.ValorAcimaDoHistorico,
                    new ConfiguracaoDeValorAcimaDoHistorico(4.5m, 7),
                    30,
                    OrigemDaRegraCandidata.Publicada),
            ]);

    [Fact]
    public void Round_trip_do_candidato_preserva_tudo()
    {
        var original = Candidato();

        var reconstruido = SerializadorDoBacktest.DesserializarCandidato(
            SerializadorDoBacktest.SerializarCandidato(original));

        Assert.Equal(original.LimiarDeRevisao, reconstruido.LimiarDeRevisao);
        Assert.Equal(original.LimiarDeBloqueio, reconstruido.LimiarDeBloqueio);

        // Records comparam por valor: esta igualdade cobre configuracao,
        // pontos, origem e qualquer campo que venha a ser adicionado.
        Assert.Equal(original.Regras.ToArray(), reconstruido.Regras.ToArray());
    }

    [Fact]
    public void O_candidato_reconstruido_continua_valido()
    {
        // Se o round-trip perdesse o tipo ou o peso, a validacao acusaria — e
        // e melhor acusar aqui do que num worker as tres da manha.
        SerializadorDoBacktest
            .DesserializarCandidato(SerializadorDoBacktest.SerializarCandidato(Candidato()))
            .Validar();
    }

    [Fact]
    public void Fracionario_sobrevive_sem_virar_ponto_flutuante()
    {
        // O multiplo da media e decimal. Um round-trip que o transformasse em
        // double devolveria 4,4999999 e a regra passaria a acionar em outro
        // ponto.
        var reconstruido = SerializadorDoBacktest.DesserializarCandidato(
            SerializadorDoBacktest.SerializarCandidato(Candidato()));

        var valor = Assert.IsType<ConfiguracaoDeValorAcimaDoHistorico>(
            reconstruido.Regras.Single(r => r.Tipo == TipoDeRegra.ValorAcimaDoHistorico)
                .Configuracao);

        Assert.Equal(4.5m, valor.MultiploDaMedia);
    }

    [Fact]
    public void Origem_desconhecida_falha_alto()
    {
        // Um valor fora do vocabulario nao pode virar um membro de enum que
        // nao existe. O `Enum.TryParse` aceitaria ate o numero cru.
        var adulterado = SerializadorDoBacktest
            .SerializarCandidato(Candidato())
            .Replace("\"Rascunho\"", "\"Inventada\"", StringComparison.Ordinal);

        var excecao = Assert.Throws<InvalidOperationException>(
            () => SerializadorDoBacktest.DesserializarCandidato(adulterado));

        Assert.Contains("vocabulario", excecao.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tipo_de_regra_desconhecido_falha_alto()
    {
        var adulterado = SerializadorDoBacktest
            .SerializarCandidato(Candidato())
            .Replace("\"VelocidadePorCliente\"", "\"RegraMagica\"", StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(
            () => SerializadorDoBacktest.DesserializarCandidato(adulterado));
    }

    [Fact]
    public void Round_trip_do_resultado_preserva_as_contagens()
    {
        var original = new ResultadoDoBacktest(
            120,
            44,
            new DistribuicaoDeDecisoes(100, 15, 5),
            new DistribuicaoDeDecisoes(90, 22, 8),
            [new MudancaDeDecisao(Decisao.Permitir, Decisao.Revisar, 10)],
            [
                new LinhaPorVeredito(
                    ResultadoDaInvestigacao.FraudeConfirmada,
                    3,
                    new DistribuicaoDeDecisoes(0, 2, 1),
                    new DistribuicaoDeDecisoes(0, 0, 3)),
                new LinhaPorVeredito(
                    null,
                    117,
                    new DistribuicaoDeDecisoes(100, 13, 4),
                    new DistribuicaoDeDecisoes(90, 22, 5)),
            ],
            [new FaixaDeScore(0, 19, 100, 90)]);

        var reconstruido = SerializadorDoBacktest.DesserializarResultado(
            SerializadorDoBacktest.SerializarResultado(original));

        Assert.Equal(original.TotalAnalisado, reconstruido.TotalAnalisado);
        Assert.Equal(original.TotalQueAcionaria, reconstruido.TotalQueAcionaria);
        Assert.Equal(original.Vigente, reconstruido.Vigente);
        Assert.Equal(original.Candidato, reconstruido.Candidato);
        Assert.Equal(original.Mudancas.ToArray(), reconstruido.Mudancas.ToArray());
        Assert.Equal(original.PorVeredito.ToArray(), reconstruido.PorVeredito.ToArray());
        Assert.Equal(original.FaixasDeScore.ToArray(), reconstruido.FaixasDeScore.ToArray());
    }

    [Fact]
    public void Veredito_nulo_sobrevive_como_nulo()
    {
        // "Sem resultado conhecido" e uma das quatro linhas, e distingui-la de
        // "Inconclusiva" e o ponto: uma e ausencia de investigacao, a outra e
        // investigacao que nao concluiu.
        var original = new ResultadoDoBacktest(
            1,
            0,
            DistribuicaoDeDecisoes.Vazia,
            DistribuicaoDeDecisoes.Vazia,
            [],
            [new LinhaPorVeredito(null, 1, DistribuicaoDeDecisoes.Vazia, DistribuicaoDeDecisoes.Vazia)],
            []);

        var reconstruido = SerializadorDoBacktest.DesserializarResultado(
            SerializadorDoBacktest.SerializarResultado(original));

        Assert.Null(reconstruido.PorVeredito.Single().Veredito);
    }

    [Fact]
    public void Decisoes_viajam_como_texto()
    {
        // O inteiro 1 muda de significado se alguem reordenar o enum; o texto
        // "Permitir" continua sendo o que sempre foi. E o mesmo contrato do
        // JSON da API.
        var json = SerializadorDoBacktest.SerializarResultado(new ResultadoDoBacktest(
            0,
            0,
            DistribuicaoDeDecisoes.Vazia,
            DistribuicaoDeDecisoes.Vazia,
            [new MudancaDeDecisao(Decisao.Permitir, Decisao.Bloquear, 1)],
            [],
            []));

        Assert.Contains("\"Permitir\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Bloquear\"", json, StringComparison.Ordinal);
    }
}
