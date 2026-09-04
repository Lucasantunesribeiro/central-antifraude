using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// O motor inteiro: soma, teto, decisao, ordem e determinismo.
///
/// Estes testes rodam sem banco, sem relogio e sem rede — o que so e possivel
/// porque o motor recebe o contexto pronto. Essa mesma propriedade e o que vai
/// permitir a Fase 9 rodar o MESMO motor sobre dados historicos no backtest,
/// em vez de reimplementar as regras.
/// </summary>
public class MotorDeRiscoTests
{
    private static readonly ConfiguracaoDeVelocidade Velocidade =
        new(MaximoDeTransacoes: 3, JanelaEmMinutos: 10);

    private static readonly ConfiguracaoDeNovoDispositivo NovoDispositivo =
        new(MinimoDeTransacoesNoHistorico: 3);

    private static readonly ConfiguracaoDeValorAcimaDoHistorico ValorAcima =
        new(MultiploDaMedia: 5m, MinimoDeTransacoesNoHistorico: 3);

    private static readonly ConfiguracaoDeDivergenciaGeografica Divergencia =
        new(MinimoDeTransacoesNoHistorico: 3);

    /// <summary>Historico normal: tres compras de 100 BRL, do mesmo aparelho, no Brasil.</summary>
    private static ContextoDeRisco HistoricoNormal() =>
        CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(minutosAntes: 60 * 24 * 3),
            CenarioDeRisco.Anterior(minutosAntes: 60 * 24 * 2),
            CenarioDeRisco.Anterior(minutosAntes: 60 * 24));

    // -----------------------------------------------------------------------
    // Score e decisao
    // -----------------------------------------------------------------------

    [Fact]
    public void Transacao_normal_recebe_score_zero_e_permitir()
    {
        var avaliacao = new MotorDeRisco().Avaliar(
            CenarioDeRisco.Transacao(),
            CenarioDeRisco.PerfilPadrao(),
            HistoricoNormal(),
            CenarioDeRisco.Referencia);

        Assert.Equal(0, avaliacao.Score);
        Assert.Equal(Decisao.Permitir, avaliacao.Decisao);
        Assert.Empty(avaliacao.Sinais);
    }

    [Fact]
    public void Um_sinal_sozinho_nunca_bloqueia_no_catalogo_padrao()
    {
        // A regra mais pesada do catalogo vale 35 pontos, e o limiar de
        // bloqueio e 70. Isso e uma escolha de produto, e este teste e o que
        // impede que ela se perca: bloquear exige ao menos duas evidencias
        // independentes, para que a investigacao humana continue tendo
        // proposito.
        var maiorPeso = CatalogoPadraoDeRisco.Definicoes.Max(d => d.Pontos);

        Assert.True(maiorPeso < CatalogoPadraoDeRisco.LimiarDeBloqueio);
    }

    [Fact]
    public void Dois_sinais_somam_e_levam_a_revisar()
    {
        // Dispositivo novo (20) + pais novo (25) = 45, acima do limiar de
        // revisao (40) e abaixo do de bloqueio (70).
        var avaliacao = new MotorDeRisco().Avaliar(
            CenarioDeRisco.Transacao(dispositivo: "disp-novo", pais: "RU"),
            CenarioDeRisco.PerfilPadrao(),
            HistoricoNormal(),
            CenarioDeRisco.Referencia);

        Assert.Equal(45, avaliacao.Score);
        Assert.Equal(Decisao.Revisar, avaliacao.Decisao);
        Assert.Equal(2, avaliacao.Sinais.Count);
    }

    [Fact]
    public void Tres_sinais_levam_a_bloquear()
    {
        // Velocidade (35) + dispositivo novo (20) + valor acima (30) = 85.
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(minutosAntes: 1),
            CenarioDeRisco.Anterior(minutosAntes: 2),
            CenarioDeRisco.Anterior(minutosAntes: 3));

        var avaliacao = new MotorDeRisco().Avaliar(
            CenarioDeRisco.Transacao(valor: 10_000m, dispositivo: "disp-novo"),
            CenarioDeRisco.PerfilPadrao(),
            contexto,
            CenarioDeRisco.Referencia);

        Assert.Equal(85, avaliacao.Score);
        Assert.Equal(Decisao.Bloquear, avaliacao.Decisao);
        Assert.Equal(3, avaliacao.Sinais.Count);
    }

    [Theory]
    [InlineData(39, Decisao.Permitir)]
    [InlineData(40, Decisao.Revisar)]
    [InlineData(69, Decisao.Revisar)]
    [InlineData(70, Decisao.Bloquear)]
    public void Limiares_sao_inclusivos_na_borda_de_baixo(int score, Decisao esperada)
    {
        // O limiar pertence a faixa mais severa: 40 ja e Revisar, 70 ja e
        // Bloquear. A alternativa seria igualmente defensavel — o que nao pode
        // e ficar indefinido, porque a mesma transacao teria decisoes
        // diferentes conforme quem leu a configuracao.
        var perfil = CenarioDeRisco.PerfilPadrao();

        Assert.Equal(esperada, perfil.DecidirPor(score));
    }

    [Fact]
    public void Score_e_limitado_a_cem_e_o_teto_fica_visivel()
    {
        // Quatro regras de 40 pontos somariam 160. O score final e 100, mas a
        // soma bruta continua registrada: uma tela que mostrasse as
        // contribuicoes sem dizer que houve teto pareceria errada em uma
        // conta simples.
        var perfil = CenarioDeRisco.Perfil(
        [
            (TipoDeRegra.VelocidadePorCliente, Velocidade, 40),
            (TipoDeRegra.NovoDispositivo, NovoDispositivo, 40),
            (TipoDeRegra.ValorAcimaDoHistorico, ValorAcima, 40),
            (TipoDeRegra.DivergenciaGeografica, Divergencia, 40),
        ]);

        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(minutosAntes: 1),
            CenarioDeRisco.Anterior(minutosAntes: 2),
            CenarioDeRisco.Anterior(minutosAntes: 3));

        var avaliacao = new MotorDeRisco().Avaliar(
            CenarioDeRisco.Transacao(valor: 10_000m, dispositivo: "disp-novo", pais: "RU"),
            perfil,
            contexto,
            CenarioDeRisco.Referencia);

        Assert.Equal(4, avaliacao.Sinais.Count);
        Assert.Equal(160, avaliacao.SomaBrutaDosPontos);
        Assert.Equal(100, avaliacao.Score);
        Assert.True(avaliacao.ScoreFoiLimitado);
        Assert.Equal(Decisao.Bloquear, avaliacao.Decisao);
    }

    // -----------------------------------------------------------------------
    // Determinismo
    // -----------------------------------------------------------------------

    [Fact]
    public void Mesma_entrada_produz_o_mesmo_resultado()
    {
        // A promessa central do CLAUDE.md secao 19. Duas execucoes do motor
        // com a MESMA transacao, o MESMO contexto e a MESMA versao de perfil
        // precisam coincidir em score, decisao, quantidade e ordem dos sinais,
        // e ate no texto de cada explicacao — porque e esse texto que fica
        // gravado e sustenta a explicabilidade historica.
        var transacao = CenarioDeRisco.Transacao(
            valor: 10_000m,
            dispositivo: "disp-novo",
            pais: "RU");

        var perfil = CenarioDeRisco.PerfilPadrao();
        var contexto = HistoricoNormal();

        var primeira = new MotorDeRisco().Avaliar(transacao, perfil, contexto, CenarioDeRisco.Referencia);
        var segunda = new MotorDeRisco().Avaliar(transacao, perfil, contexto, CenarioDeRisco.Referencia);

        Assert.Equal(primeira.Score, segunda.Score);
        Assert.Equal(primeira.Decisao, segunda.Decisao);
        Assert.Equal(primeira.Sinais.Count, segunda.Sinais.Count);

        foreach (var (esquerda, direita) in primeira.Sinais.Zip(segunda.Sinais))
        {
            Assert.Equal(esquerda.Tipo, direita.Tipo);
            Assert.Equal(esquerda.Pontos, direita.Pontos);
            Assert.Equal(esquerda.Explicacao, direita.Explicacao);
            Assert.Equal(esquerda.VersaoDeRegraId, direita.VersaoDeRegraId);
            Assert.Equal(esquerda.DadosDaEvidencia, direita.DadosDaEvidencia);
        }
    }

    [Fact]
    public void Ordem_dos_sinais_nao_depende_da_ordem_das_regras_no_perfil()
    {
        // O mesmo conjunto de regras, declarado em ordens diferentes, produz
        // sinais na mesma ordem. Sem isso, duas instalacoes com configuracao
        // identica devolveriam avaliacoes que parecem diferentes, e comparar
        // duas execucoes deixaria de ser confiavel.
        var direta = CenarioDeRisco.Perfil(
        [
            (TipoDeRegra.VelocidadePorCliente, Velocidade, 10),
            (TipoDeRegra.NovoDispositivo, NovoDispositivo, 10),
            (TipoDeRegra.DivergenciaGeografica, Divergencia, 10),
        ]);

        var invertida = CenarioDeRisco.Perfil(
        [
            (TipoDeRegra.DivergenciaGeografica, Divergencia, 10),
            (TipoDeRegra.NovoDispositivo, NovoDispositivo, 10),
            (TipoDeRegra.VelocidadePorCliente, Velocidade, 10),
        ]);

        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(minutosAntes: 1),
            CenarioDeRisco.Anterior(minutosAntes: 2),
            CenarioDeRisco.Anterior(minutosAntes: 3));

        var transacao = CenarioDeRisco.Transacao(dispositivo: "disp-novo", pais: "RU");
        var motor = new MotorDeRisco();

        var primeira = motor.Avaliar(transacao, direta, contexto, CenarioDeRisco.Referencia);
        var segunda = motor.Avaliar(transacao, invertida, contexto, CenarioDeRisco.Referencia);

        Assert.Equal(
            primeira.Sinais.Select(s => s.Tipo),
            segunda.Sinais.Select(s => s.Tipo));
    }

    // -----------------------------------------------------------------------
    // Rastreabilidade e isolamento
    // -----------------------------------------------------------------------

    [Fact]
    public void Cada_sinal_aponta_para_a_versao_de_regra_que_o_produziu()
    {
        // E o que permite abrir uma avaliacao de meses atras e explica-la com
        // a configuracao daquele momento, mesmo depois de a regra ter sido
        // republicada (CLAUDE.md secao 17).
        var perfil = CenarioDeRisco.PerfilPadrao();

        var avaliacao = new MotorDeRisco().Avaliar(
            CenarioDeRisco.Transacao(dispositivo: "disp-novo", pais: "RU"),
            perfil,
            HistoricoNormal(),
            CenarioDeRisco.Referencia);

        Assert.NotEmpty(avaliacao.Sinais);
        Assert.Equal(perfil.Id, avaliacao.VersaoDePerfilId);
        Assert.Equal(perfil.Numero, avaliacao.NumeroDaVersaoDePerfil);
        Assert.Equal(AvaliacaoDeRisco.VersaoDoMotor, avaliacao.VersaoDoMotorUsada);

        foreach (var sinal in avaliacao.Sinais)
        {
            var versao = perfil.VersoesDeRegra.Single(v => v.Id == sinal.VersaoDeRegraId);

            Assert.Equal(versao.Tipo, sinal.Tipo);
            Assert.Equal(versao.Pontos, sinal.Pontos);
            Assert.Equal(versao.Numero, sinal.NumeroDaVersaoDeRegra);
            Assert.Equal(versao.RegraId, sinal.RegraId);
            Assert.NotEmpty(sinal.Explicacao);
        }
    }

    [Fact]
    public void Motor_recusa_avaliar_transacao_com_perfil_de_outra_organizacao()
    {
        // Este e o vazamento entre tenants mais silencioso que existiria: o
        // resultado sairia plausivel, com score e sinais, e ninguem notaria
        // que as regras vieram de outro cliente.
        var perfilDeOutraOrganizacao = CenarioDeRisco.PerfilPadrao(Identificador.Novo());

        Assert.Throws<ViolacaoDeInvariante>(() => new MotorDeRisco().Avaliar(
            CenarioDeRisco.Transacao(),
            perfilDeOutraOrganizacao,
            HistoricoNormal(),
            CenarioDeRisco.Referencia));
    }

    [Fact]
    public void Motor_falha_alto_diante_de_tipo_de_regra_que_nao_sabe_executar()
    {
        // Ignorar a regra desconhecida em silencio seria pior: a avaliacao
        // sairia com score menor do que o perfil pede e a explicacao nao
        // mencionaria a ausencia.
        var motorParcial = new MotorDeRisco([new AvaliadorDeVelocidade()]);

        var perfil = CenarioDeRisco.Perfil(
        [
            (TipoDeRegra.VelocidadePorCliente, Velocidade, 10),
            (TipoDeRegra.NovoDispositivo, NovoDispositivo, 10),
        ]);

        Assert.Throws<ViolacaoDeInvariante>(() => motorParcial.Avaliar(
            CenarioDeRisco.Transacao(),
            perfil,
            HistoricoNormal(),
            CenarioDeRisco.Referencia));
    }

    [Fact]
    public void Motor_padrao_cobre_todo_o_catalogo_de_tipos()
    {
        // Se alguem adicionar um TipoDeRegra sem registrar o avaliador, este
        // teste quebra aqui — e nao em producao, na primeira transacao de um
        // tenant que publicou a regra nova.
        var suportados = new MotorDeRisco().TiposSuportados;

        Assert.Equal(
            Enum.GetValues<TipoDeRegra>().Order().ToList(),
            suportados.Order().ToList());
    }
}
