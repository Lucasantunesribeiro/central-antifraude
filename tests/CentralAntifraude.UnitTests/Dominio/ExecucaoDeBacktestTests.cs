using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Backtests;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// O ciclo de vida de uma execucao de backtest.
///
/// **O que estes testes protegem e o contrato de estado**, porque e ele que
/// substitui a Inbox no consumidor da fila de backtests: se "concluir uma
/// execucao ja concluida" deixasse de ser recusado, uma reentrega passaria a
/// reescrever um resultado que alguem ja leu.
///
/// Sem banco e sem relogio real: o que esta sendo verificado sao as
/// invariantes, nao a persistencia delas.
/// </summary>
public class ExecucaoDeBacktestTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Autor = Guid.CreateVersion7();
    private static readonly Guid VersaoDoPerfil = Guid.CreateVersion7();
    private static readonly DateTimeOffset Agora = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Depois = Agora.AddMinutes(5);

    private static RegraCandidata Regra(
        int pontos = 35,
        OrigemDaRegraCandidata origem = OrigemDaRegraCandidata.Rascunho,
        Guid? regraId = null) =>
        new(
            regraId ?? Guid.CreateVersion7(),
            "Velocidade por cliente",
            TipoDeRegra.VelocidadePorCliente,
            new ConfiguracaoDeVelocidade(3, 10),
            pontos,
            origem);

    private static PerfilCandidato Candidato(
        int revisao = 40,
        int bloqueio = 70,
        params RegraCandidata[] regras) =>
        new(revisao, bloqueio, regras.Length == 0 ? [Regra()] : regras);

    private static ExecucaoDeBacktest Nova(PerfilCandidato? candidato = null) =>
        ExecucaoDeBacktest.Solicitar(
            Tenant,
            "Rascunho de \"Velocidade por cliente\"",
            regraCandidataId: Guid.CreateVersion7(),
            candidato ?? Candidato(),
            VersaoDoPerfil,
            numeroDaVersaoDePerfilVigente: 3,
            Agora.AddDays(-30),
            Agora,
            Autor,
            "Supervisora de Teste",
            Agora);

    private static ResultadoDoBacktest Resultado() =>
        new(
            10,
            4,
            DistribuicaoDeDecisoes.Vazia,
            DistribuicaoDeDecisoes.Vazia,
            [],
            [],
            []);

    // -----------------------------------------------------------------------
    // Nascimento
    // -----------------------------------------------------------------------

    [Fact]
    public void Execucao_nasce_pendente_e_sem_resultado()
    {
        var execucao = Nova();

        Assert.Equal(StatusDoBacktest.Pendente, execucao.Status);
        Assert.Null(execucao.Resultado);
        Assert.Null(execucao.IniciadaEm);
        Assert.Null(execucao.ConcluidaEm);
        Assert.False(execucao.EstaEncerrada);
    }

    [Fact]
    public void Janela_invertida_e_recusada()
    {
        // Uma janela de duracao zero ou invertida "concluiria com sucesso"
        // sobre zero transacao — e um resultado vazio parece resposta.
        var excecao = Assert.Throws<ViolacaoDeInvariante>(() =>
            ExecucaoDeBacktest.Solicitar(
                Tenant,
                "Teste",
                null,
                Candidato(),
                VersaoDoPerfil,
                1,
                Agora,
                Agora.AddDays(-1),
                Autor,
                "Supervisora",
                Agora));

        Assert.Contains("depois do inicio", excecao.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execucao_exige_a_versao_de_perfil_de_comparacao()
    {
        // Sem baseline nao ha o que comparar, e "quantas decisoes mudariam"
        // deixaria de ter significado.
        Assert.Throws<ViolacaoDeInvariante>(() =>
            ExecucaoDeBacktest.Solicitar(
                Tenant,
                "Teste",
                null,
                Candidato(),
                Guid.Empty,
                1,
                Agora.AddDays(-1),
                Agora,
                Autor,
                "Supervisora",
                Agora));
    }

    [Fact]
    public void Descricao_longa_e_cortada_em_vez_de_recusada()
    {
        var execucao = ExecucaoDeBacktest.Solicitar(
            Tenant,
            new string('x', 500),
            null,
            Candidato(),
            VersaoDoPerfil,
            1,
            Agora.AddDays(-1),
            Agora,
            Autor,
            "Supervisora",
            Agora);

        Assert.Equal(ExecucaoDeBacktest.TamanhoMaximoDaDescricao, execucao.Descricao.Length);
    }

    // -----------------------------------------------------------------------
    // Perfil candidato
    // -----------------------------------------------------------------------

    [Fact]
    public void Candidato_sem_regra_nenhuma_e_recusado()
    {
        // Um perfil sem regra pontuaria tudo com zero: o backtest diria
        // "nada muda" sobre um motor cego.
        var excecao = Assert.Throws<ViolacaoDeInvariante>(() =>
            Nova(new PerfilCandidato(40, 70, [])));

        Assert.Contains("ao menos uma regra", excecao.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Candidato_com_a_mesma_regra_duas_vezes_e_recusado()
    {
        var regraId = Guid.CreateVersion7();

        Assert.Throws<ViolacaoDeInvariante>(() =>
            Nova(Candidato(regras: [Regra(regraId: regraId), Regra(regraId: regraId)])));
    }

    [Theory]
    [InlineData(70, 40)]
    [InlineData(50, 50)]
    [InlineData(0, 70)]
    [InlineData(40, 101)]
    public void Limiares_impossiveis_sao_recusados(int revisao, int bloqueio)
    {
        // Mesmas invariantes de uma versao de perfil de verdade: simular o que
        // nao poderia ser publicado descreveria um estado inalcancavel.
        Assert.Throws<ViolacaoDeInvariante>(() => Nova(Candidato(revisao, bloqueio)));
    }

    [Fact]
    public void Configuracao_de_outro_tipo_e_recusada()
    {
        var torta = new RegraCandidata(
            Guid.CreateVersion7(),
            "Torta",
            TipoDeRegra.NovoDispositivo,
            new ConfiguracaoDeVelocidade(3, 10),
            20,
            OrigemDaRegraCandidata.Rascunho);

        Assert.Throws<ViolacaoDeInvariante>(() => Nova(Candidato(regras: [torta])));
    }

    [Fact]
    public void Alteradas_lista_somente_o_que_veio_do_rascunho()
    {
        // E o que a tela usa para dizer "esta e a mudanca": o resto do perfil
        // candidato e a configuracao que ja esta valendo.
        var candidato = Candidato(regras:
        [
            Regra(origem: OrigemDaRegraCandidata.Publicada),
            Regra(origem: OrigemDaRegraCandidata.Rascunho),
            Regra(origem: OrigemDaRegraCandidata.Publicada),
        ]);

        Assert.Single(candidato.Alteradas);
        Assert.Equal(OrigemDaRegraCandidata.Rascunho, candidato.Alteradas[0].Origem);
    }

    // -----------------------------------------------------------------------
    // Transicoes
    // -----------------------------------------------------------------------

    [Fact]
    public void Iniciar_marca_o_inicio_e_sobe_a_versao()
    {
        var execucao = Nova();
        var versaoAntes = execucao.Versao;

        execucao.Iniciar(Depois);

        Assert.Equal(StatusDoBacktest.Executando, execucao.Status);
        Assert.Equal(Depois, execucao.IniciadaEm);

        // O token sobe em toda transicao: e ele que faz um segundo worker
        // perder a corrida e que arbitra o cancelamento durante a execucao.
        Assert.Equal(versaoAntes + 1, execucao.Versao);
    }

    [Fact]
    public void Iniciar_duas_vezes_e_recusado()
    {
        var execucao = Nova();
        execucao.Iniciar(Agora);

        Assert.Throws<ViolacaoDeInvariante>(() => execucao.Iniciar(Depois));
    }

    [Fact]
    public void Retomar_so_vale_para_o_que_esta_em_andamento()
    {
        var execucao = Nova();

        Assert.Throws<ViolacaoDeInvariante>(() => execucao.Retomar(Depois));

        execucao.Iniciar(Agora);
        execucao.Retomar(Depois);

        Assert.Equal(StatusDoBacktest.Executando, execucao.Status);
        Assert.Equal(Depois, execucao.IniciadaEm);
    }

    [Fact]
    public void Concluir_guarda_o_resultado()
    {
        var execucao = Nova();
        execucao.Iniciar(Agora);
        execucao.Concluir(Resultado(), Depois);

        Assert.Equal(StatusDoBacktest.Concluida, execucao.Status);
        Assert.Equal(10, execucao.Resultado!.TotalAnalisado);
        Assert.Equal(Depois, execucao.ConcluidaEm);
        Assert.True(execucao.EstaEncerrada);
    }

    [Fact]
    public void Concluir_sem_ter_iniciado_e_recusado()
    {
        var execucao = Nova();

        Assert.Throws<ViolacaoDeInvariante>(() => execucao.Concluir(Resultado(), Depois));
    }

    [Fact]
    public void Concluir_duas_vezes_e_recusado()
    {
        // Esta e a invariante que substitui a Inbox no consumidor: uma
        // reentrega nao pode reescrever um resultado ja lido.
        var execucao = Nova();
        execucao.Iniciar(Agora);
        execucao.Concluir(Resultado(), Depois);

        Assert.Throws<ViolacaoDeInvariante>(() => execucao.Concluir(Resultado(), Depois));
    }

    [Fact]
    public void Falhar_registra_o_motivo()
    {
        var execucao = Nova();
        execucao.Iniciar(Agora);
        execucao.Falhar("A janela passou do limite.", Depois);

        Assert.Equal(StatusDoBacktest.Falhou, execucao.Status);
        Assert.Equal("A janela passou do limite.", execucao.MensagemDeErro);
        Assert.Null(execucao.Resultado);
    }

    [Fact]
    public void Uma_execucao_pendente_pode_falhar_sem_ter_iniciado()
    {
        // O caminho de quem descobre o problema antes de comecar — limite
        // estourado entre o pedido e a execucao, por exemplo.
        var execucao = Nova();
        execucao.Falhar("Perfil de comparacao sumiu.", Depois);

        Assert.Equal(StatusDoBacktest.Falhou, execucao.Status);
    }

    [Fact]
    public void Cancelar_vale_antes_e_durante()
    {
        var pendente = Nova();
        pendente.Cancelar(Depois);
        Assert.Equal(StatusDoBacktest.Cancelada, pendente.Status);

        var emAndamento = Nova();
        emAndamento.Iniciar(Agora);
        emAndamento.Cancelar(Depois);
        Assert.Equal(StatusDoBacktest.Cancelada, emAndamento.Status);
    }

    [Fact]
    public void Cancelar_o_que_ja_terminou_e_recusado()
    {
        // Reescrever um resultado que alguem ja leu apagaria o que a pessoa
        // viu.
        var execucao = Nova();
        execucao.Iniciar(Agora);
        execucao.Concluir(Resultado(), Depois);

        var excecao = Assert.Throws<ViolacaoDeInvariante>(() => execucao.Cancelar(Depois));

        Assert.Contains("Concluida", excecao.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelar_duas_vezes_e_recusado_com_mensagem_propria()
    {
        var execucao = Nova();
        execucao.Cancelar(Agora);

        var excecao = Assert.Throws<ViolacaoDeInvariante>(() => execucao.Cancelar(Depois));

        Assert.Contains("ja foi cancelada", excecao.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Uma_execucao_cancelada_nao_conclui_nem_falha()
    {
        var execucao = Nova();
        execucao.Iniciar(Agora);
        execucao.Cancelar(Depois);

        Assert.Throws<ViolacaoDeInvariante>(() => execucao.Concluir(Resultado(), Depois));
        Assert.Throws<ViolacaoDeInvariante>(() => execucao.Falhar("tarde demais", Depois));
    }
}
