using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// A politica de alertas, sem banco, sem fila e sem relogio real.
///
/// **Ela nao e um segundo motor de risco.** Nao le historico, nao soma pontos
/// e nao reavalia nada: recebe a decisao que o motor ja tomou e responde uma
/// pergunta operacional — isto precisa de olho humano, e com que urgencia?
///
/// Ser uma funcao pura e o que torna estes testes possiveis sem infraestrutura
/// nenhuma, e o que garante que a resposta do consumidor nunca discorde do
/// recibo que o integrador ja recebeu.
/// </summary>
public class PoliticaDeAlertasTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Evento = Guid.CreateVersion7();
    private static readonly DateTimeOffset Agora =
        new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    // -----------------------------------------------------------------------
    // O mapa decisao -> prioridade
    // -----------------------------------------------------------------------

    [Fact]
    public void Permitir_nao_gera_alerta()
    {
        // Uma fila que recebesse toda transacao permitida deixaria de ser fila
        // de trabalho e viraria um espelho da tabela de transacoes — que ja
        // existe, na tela de Transacoes.
        Assert.Null(PoliticaDeAlertas.Classificar(Decisao.Permitir));
        Assert.Null(Avaliar(Decisao.Permitir, score: 10));
    }

    [Theory]
    [InlineData(Decisao.Revisar, PrioridadeDeAlerta.Media)]
    [InlineData(Decisao.Bloquear, PrioridadeDeAlerta.Alta)]
    public void Revisar_e_bloquear_geram_alerta_com_a_prioridade_da_politica(
        Decisao decisao,
        PrioridadeDeAlerta esperada)
    {
        Assert.Equal(esperada, PoliticaDeAlertas.Classificar(decisao));

        var alerta = Avaliar(decisao, score: decisao == Decisao.Bloquear ? 80 : 45);

        Assert.NotNull(alerta);
        Assert.Equal(esperada, alerta.Prioridade);
        Assert.Equal(decisao, alerta.Decisao);
    }

    [Fact]
    public void Decisao_desconhecida_falha_alto_em_vez_de_silenciar()
    {
        // Uma decisao nova sem linha na politica nao pode virar "nao alerta".
        // Silenciar seria deixar de alertar sobre um resultado que ninguem
        // sabe classificar — o pior desfecho possivel numa fila de fraude.
        Assert.Throws<ViolacaoDeInvariante>(() => PoliticaDeAlertas.Classificar((Decisao)99));
    }

    // -----------------------------------------------------------------------
    // O que o alerta guarda
    // -----------------------------------------------------------------------

    [Fact]
    public void O_alerta_copia_a_avaliacao_e_guarda_a_procedencia()
    {
        var conteudo = Conteudo(Decisao.Bloquear, score: 75);

        var alerta = PoliticaDeAlertas.Avaliar(Tenant, conteudo, Evento, "corr-1", Agora);

        Assert.NotNull(alerta);

        // Da avaliacao: o que a fila filtra e ordena sem precisar de juncao.
        Assert.Equal(conteudo.AvaliacaoId, alerta.AvaliacaoId);
        Assert.Equal(conteudo.TransacaoId, alerta.TransacaoId);
        Assert.Equal(75, alerta.Score);
        Assert.Equal(conteudo.AvaliadaEm, alerta.AvaliadaEm);

        // Da procedencia: como voltar deste alerta ate a requisicao que o
        // originou, depois que o log tiver sumido com a retencao.
        Assert.Equal(Tenant, alerta.OrganizacaoId);
        Assert.Equal(Evento, alerta.EventoId);
        Assert.Equal("corr-1", alerta.IdDeCorrelacao);
        Assert.Equal(PoliticaDeAlertas.Versao, alerta.VersaoDaPolitica);

        // O alerta nasce aberto: nada nesta fase o move.
        Assert.Equal(StatusDoAlerta.Aberto, alerta.Status);
    }

    [Fact]
    public void O_alerta_distingue_quando_o_motor_decidiu_de_quando_ele_entrou_na_fila()
    {
        // O caminho e assincrono, e a distancia entre os dois e a latencia
        // real do backbone. Trata-los como iguais esconderia atraso de
        // processamento.
        var conteudo = Conteudo(Decisao.Revisar, score: 45) with
        {
            AvaliadaEm = Agora.AddSeconds(-30),
        };

        var alerta = PoliticaDeAlertas.Avaliar(Tenant, conteudo, Evento, "corr-2", Agora);

        Assert.NotNull(alerta);
        Assert.Equal(Agora.AddSeconds(-30), alerta.AvaliadaEm);
        Assert.Equal(Agora, alerta.CriadoEm);
        Assert.True(alerta.CriadoEm > alerta.AvaliadaEm);
    }

    [Fact]
    public void Mesma_entrada_produz_o_mesmo_alerta()
    {
        // Determinismo: o unico campo que difere e o identificador, que e
        // sorteado por definicao. Se a politica dependesse de qualquer coisa
        // fora dos argumentos, este teste denunciaria.
        var conteudo = Conteudo(Decisao.Revisar, score: 41);

        var primeiro = PoliticaDeAlertas.Avaliar(Tenant, conteudo, Evento, "corr-3", Agora);
        var segundo = PoliticaDeAlertas.Avaliar(Tenant, conteudo, Evento, "corr-3", Agora);

        Assert.NotNull(primeiro);
        Assert.NotNull(segundo);
        Assert.NotEqual(primeiro.Id, segundo.Id);

        Assert.Equal(primeiro.Prioridade, segundo.Prioridade);
        Assert.Equal(primeiro.Score, segundo.Score);
        Assert.Equal(primeiro.AvaliacaoId, segundo.AvaliacaoId);
        Assert.Equal(primeiro.VersaoDaPolitica, segundo.VersaoDaPolitica);
        Assert.Equal(primeiro.CriadoEm, segundo.CriadoEm);
    }

    // -----------------------------------------------------------------------
    // Invariantes do alerta
    // -----------------------------------------------------------------------

    [Fact]
    public void Alerta_sem_evento_de_origem_e_recusado()
    {
        // Sem o evento nao ha como refazer o caminho ate a requisicao, e o
        // alerta viraria um numero na tela sem procedencia.
        var conteudo = Conteudo(Decisao.Bloquear, score: 90);

        Assert.Throws<ViolacaoDeInvariante>(
            () => PoliticaDeAlertas.Avaliar(Tenant, conteudo, Guid.Empty, "corr", Agora));
    }

    [Fact]
    public void Alerta_sem_organizacao_e_recusado()
    {
        var conteudo = Conteudo(Decisao.Bloquear, score: 90);

        Assert.Throws<ViolacaoDeInvariante>(
            () => PoliticaDeAlertas.Avaliar(Guid.Empty, conteudo, Evento, "corr", Agora));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Score_fora_da_faixa_do_produto_e_recusado(int score)
    {
        // O score e limitado a 0..100 pelo motor. Um valor fora disso chegando
        // aqui significa evento corrompido, e nao alerta legitimo.
        var conteudo = Conteudo(Decisao.Bloquear, score);

        Assert.Throws<ViolacaoDeInvariante>(
            () => PoliticaDeAlertas.Avaliar(Tenant, conteudo, Evento, "corr", Agora));
    }

    // -----------------------------------------------------------------------

    private static Alerta? Avaliar(Decisao decisao, int score) =>
        PoliticaDeAlertas.Avaliar(Tenant, Conteudo(decisao, score), Evento, "corr", Agora);

    private static TransacaoAvaliadaV1 Conteudo(Decisao decisao, int score) => new(
        TransacaoId: Guid.CreateVersion7(),
        IdentificadorExterno: "pedido-1",
        ClienteExternoId: "cli-1",
        Valor: 100m,
        Moeda: "BRL",
        OcorridaEm: Agora.AddMinutes(-5),
        RecebidaEm: Agora.AddMinutes(-4),
        AvaliacaoId: Guid.CreateVersion7(),
        Score: score,
        Decisao: decisao,
        AvaliadaEm: Agora.AddMinutes(-4),
        VersaoDePerfilId: Guid.CreateVersion7(),
        NumeroDaVersaoDePerfil: 1,
        Sinais: []);
}
