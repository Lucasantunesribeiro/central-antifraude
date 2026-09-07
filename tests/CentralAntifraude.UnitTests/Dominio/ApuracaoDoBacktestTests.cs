using CentralAntifraude.Domain.Backtests;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// A apuracao do backtest.
///
/// **O que precisa ser protegido aqui e a honestidade dos numeros**
/// (ROADMAP 9.6). Cada contagem tem um denominador visivel, e nenhuma delas e
/// apresentada como taxa de acerto: a maioria das transacoes nunca foi
/// investigada, e as que foram nao sao amostra aleatoria. Um numero chamado de
/// "precisao" aqui seria uma afirmacao que os dados nao sustentam.
/// </summary>
public class ApuracaoDoBacktestTests
{
    private static ObservacaoDoBacktest Observacao(
        int scoreVigente,
        Decisao vigente,
        int scoreCandidato,
        Decisao candidato,
        bool acionou = true,
        ResultadoDaInvestigacao? veredito = null) =>
        new(scoreVigente, vigente, scoreCandidato, candidato, acionou, veredito);

    private static ResultadoDoBacktest Apurar(params ObservacaoDoBacktest[] observacoes)
    {
        var apuracao = new ApuracaoDoBacktest();

        foreach (var observacao in observacoes)
        {
            apuracao.Registrar(observacao);
        }

        return apuracao.Concluir();
    }

    [Fact]
    public void Sem_observacao_o_resultado_e_zerado_e_nao_nulo()
    {
        // Uma janela sem transacao nenhuma e um resultado legitimo — "nao
        // havia o que analisar" — e nao uma falha. A tela precisa poder dizer
        // isso.
        var resultado = Apurar();

        Assert.Equal(0, resultado.TotalAnalisado);
        Assert.Empty(resultado.Mudancas);
        Assert.Empty(resultado.PorVeredito);
        Assert.Equal(5, resultado.FaixasDeScore.Count);
    }

    [Fact]
    public void Distribuicoes_contam_os_dois_perfis_separadamente()
    {
        var resultado = Apurar(
            Observacao(10, Decisao.Permitir, 45, Decisao.Revisar),
            Observacao(45, Decisao.Revisar, 75, Decisao.Bloquear),
            Observacao(80, Decisao.Bloquear, 80, Decisao.Bloquear));

        Assert.Equal(3, resultado.TotalAnalisado);
        Assert.Equal(new DistribuicaoDeDecisoes(1, 1, 1), resultado.Vigente);
        Assert.Equal(new DistribuicaoDeDecisoes(0, 1, 2), resultado.Candidato);
    }

    [Fact]
    public void Somente_o_que_muda_aparece_na_lista_de_mudancas()
    {
        // "Revisar continua Revisar" nao e informacao. O Supervisor quer ver o
        // que passaria a ser diferente se ele publicasse.
        var resultado = Apurar(
            Observacao(45, Decisao.Revisar, 45, Decisao.Revisar),
            Observacao(10, Decisao.Permitir, 45, Decisao.Revisar),
            Observacao(15, Decisao.Permitir, 50, Decisao.Revisar),
            Observacao(45, Decisao.Revisar, 10, Decisao.Permitir));

        Assert.Equal(2, resultado.Mudancas.Count);
        Assert.Equal(3, resultado.TotalDeMudancas);

        var maisRigido = resultado.Mudancas.Single(
            m => m.De == Decisao.Permitir && m.Para == Decisao.Revisar);

        Assert.Equal(2, maisRigido.Quantidade);
    }

    [Fact]
    public void Mudancas_saem_em_ordem_declarada()
    {
        // Duas execucoes com os mesmos numeros precisam produzir o mesmo
        // documento: sem ordem fixa, comparar duas execucoes viraria leitura
        // de diferenca falsa.
        var primeira = Apurar(
            Observacao(80, Decisao.Bloquear, 10, Decisao.Permitir),
            Observacao(10, Decisao.Permitir, 45, Decisao.Revisar),
            Observacao(45, Decisao.Revisar, 80, Decisao.Bloquear));

        var segunda = Apurar(
            Observacao(45, Decisao.Revisar, 80, Decisao.Bloquear),
            Observacao(80, Decisao.Bloquear, 10, Decisao.Permitir),
            Observacao(10, Decisao.Permitir, 45, Decisao.Revisar));

        Assert.Equal(
            primeira.Mudancas.Select(m => (m.De, m.Para)).ToArray(),
            segunda.Mudancas.Select(m => (m.De, m.Para)).ToArray());

        Assert.Equal(Decisao.Permitir, primeira.Mudancas[0].De);
        Assert.Equal(Decisao.Bloquear, primeira.Mudancas[^1].De);
    }

    [Fact]
    public void Total_que_acionaria_conta_transacoes_com_ao_menos_um_sinal()
    {
        var resultado = Apurar(
            Observacao(0, Decisao.Permitir, 0, Decisao.Permitir, acionou: false),
            Observacao(0, Decisao.Permitir, 20, Decisao.Permitir, acionou: true),
            Observacao(0, Decisao.Permitir, 45, Decisao.Revisar, acionou: true));

        Assert.Equal(3, resultado.TotalAnalisado);
        Assert.Equal(2, resultado.TotalQueAcionaria);
    }

    [Fact]
    public void Vereditos_saem_em_ordem_fixa_com_o_desconhecido_por_ultimo()
    {
        // "Sem resultado conhecido" costuma ser a maioria e nao pode encabecar
        // a leitura: quem abre a tela precisa ver primeiro o que a
        // investigacao apurou.
        var resultado = Apurar(
            Observacao(10, Decisao.Permitir, 10, Decisao.Permitir),
            Observacao(80, Decisao.Bloquear, 80, Decisao.Bloquear,
                veredito: ResultadoDaInvestigacao.FraudeConfirmada),
            Observacao(45, Decisao.Revisar, 10, Decisao.Permitir,
                veredito: ResultadoDaInvestigacao.Legitima),
            Observacao(45, Decisao.Revisar, 45, Decisao.Revisar,
                veredito: ResultadoDaInvestigacao.Inconclusiva));

        Assert.Equal(
            new ResultadoDaInvestigacao?[]
            {
                ResultadoDaInvestigacao.FraudeConfirmada,
                ResultadoDaInvestigacao.Legitima,
                ResultadoDaInvestigacao.Inconclusiva,
                null,
            },
            resultado.PorVeredito.Select(l => l.Veredito).ToArray());
    }

    [Fact]
    public void Linha_de_veredito_sem_ocorrencia_nao_aparece()
    {
        // Dizer "0 fraudes confirmadas, das quais 0 seriam bloqueadas" e
        // ruido, e ruido numa tela de decisao custa atencao.
        var resultado = Apurar(
            Observacao(10, Decisao.Permitir, 10, Decisao.Permitir),
            Observacao(80, Decisao.Bloquear, 80, Decisao.Bloquear,
                veredito: ResultadoDaInvestigacao.FraudeConfirmada));

        Assert.Equal(2, resultado.PorVeredito.Count);
        Assert.DoesNotContain(
            resultado.PorVeredito,
            l => l.Veredito == ResultadoDaInvestigacao.Legitima);
    }

    [Fact]
    public void Cada_linha_de_veredito_traz_o_proprio_denominador()
    {
        // Esta e a diferenca entre contagem honesta e taxa inventada: a linha
        // diz "de 3 fraudes confirmadas, o candidato bloquearia 2".
        var resultado = Apurar(
            Observacao(45, Decisao.Revisar, 80, Decisao.Bloquear,
                veredito: ResultadoDaInvestigacao.FraudeConfirmada),
            Observacao(45, Decisao.Revisar, 80, Decisao.Bloquear,
                veredito: ResultadoDaInvestigacao.FraudeConfirmada),
            Observacao(45, Decisao.Revisar, 45, Decisao.Revisar,
                veredito: ResultadoDaInvestigacao.FraudeConfirmada));

        var fraude = resultado.PorVeredito.Single(
            l => l.Veredito == ResultadoDaInvestigacao.FraudeConfirmada);

        Assert.Equal(3, fraude.Total);
        Assert.Equal(new DistribuicaoDeDecisoes(0, 3, 0), fraude.Vigente);
        Assert.Equal(new DistribuicaoDeDecisoes(0, 1, 2), fraude.Candidato);
    }

    [Fact]
    public void Falso_positivo_aparece_como_falso_positivo()
    {
        // O caso que o CLAUDE.md secao 94 exige da demonstracao: decisao
        // automatica Revisar, veredito humano Legitima. O backtest precisa
        // mostrar que o candidato deixaria de revisa-la.
        var resultado = Apurar(
            Observacao(45, Decisao.Revisar, 10, Decisao.Permitir,
                veredito: ResultadoDaInvestigacao.Legitima));

        var legitima = resultado.PorVeredito.Single(
            l => l.Veredito == ResultadoDaInvestigacao.Legitima);

        Assert.Equal(new DistribuicaoDeDecisoes(0, 1, 0), legitima.Vigente);
        Assert.Equal(new DistribuicaoDeDecisoes(1, 0, 0), legitima.Candidato);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(19, 0)]
    [InlineData(20, 1)]
    [InlineData(39, 1)]
    [InlineData(40, 2)]
    [InlineData(59, 2)]
    [InlineData(60, 3)]
    [InlineData(79, 3)]
    [InlineData(80, 4)]
    [InlineData(100, 4)]
    public void Cada_score_cai_na_faixa_certa(int score, int faixaEsperada)
    {
        var resultado = Apurar(Observacao(score, Decisao.Permitir, score, Decisao.Permitir));

        Assert.Equal(1, resultado.FaixasDeScore[faixaEsperada].Vigente);
        Assert.Equal(1, resultado.FaixasDeScore[faixaEsperada].Candidato);
        Assert.Equal(1, resultado.FaixasDeScore.Sum(f => f.Vigente));
    }

    [Fact]
    public void As_faixas_cobrem_zero_a_cem_sem_buraco_nem_sobreposicao()
    {
        var resultado = Apurar();

        Assert.Equal(0, resultado.FaixasDeScore[0].De);
        Assert.Equal(100, resultado.FaixasDeScore[^1].Ate);

        for (var indice = 1; indice < resultado.FaixasDeScore.Count; indice++)
        {
            Assert.Equal(
                resultado.FaixasDeScore[indice - 1].Ate + 1,
                resultado.FaixasDeScore[indice].De);
        }
    }

    [Fact]
    public void A_apuracao_e_deterministica()
    {
        // Mesmas observacoes, mesma saida. Sem isso, comparar duas execucoes
        // do mesmo periodo apontaria diferencas que nao existem.
        ObservacaoDoBacktest[] observacoes =
        [
            Observacao(10, Decisao.Permitir, 45, Decisao.Revisar,
                veredito: ResultadoDaInvestigacao.Legitima),
            Observacao(80, Decisao.Bloquear, 80, Decisao.Bloquear,
                veredito: ResultadoDaInvestigacao.FraudeConfirmada),
            Observacao(45, Decisao.Revisar, 10, Decisao.Permitir),
        ];

        var primeira = Apurar(observacoes);
        var segunda = Apurar(observacoes);

        // Comparado membro a membro de proposito: a igualdade sintetizada de
        // um record compara as LISTAS por referencia, entao `Assert.Equal`
        // sobre os dois resultados passaria por um motivo errado — ou, pior,
        // falharia sobre dois documentos identicos.
        Assert.Equal(primeira.TotalAnalisado, segunda.TotalAnalisado);
        Assert.Equal(primeira.TotalQueAcionaria, segunda.TotalQueAcionaria);
        Assert.Equal(primeira.Vigente, segunda.Vigente);
        Assert.Equal(primeira.Candidato, segunda.Candidato);
        Assert.Equal(primeira.Mudancas.ToArray(), segunda.Mudancas.ToArray());
        Assert.Equal(primeira.PorVeredito.ToArray(), segunda.PorVeredito.ToArray());
        Assert.Equal(primeira.FaixasDeScore.ToArray(), segunda.FaixasDeScore.ToArray());
    }
}
