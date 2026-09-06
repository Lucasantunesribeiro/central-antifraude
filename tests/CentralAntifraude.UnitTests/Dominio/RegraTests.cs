using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// O ciclo de vida de uma regra: rascunho, publicacao, substituicao.
///
/// **A regra muda; a versao publicada nao.** E essa separacao que sustenta a
/// explicabilidade historica (CLAUDE.md secoes 17 e 23): uma avaliacao de
/// marco aponta para a versao que valia em marco, e nenhum ajuste feito em
/// setembro alcanca aquele registro.
///
/// Sem banco, sem HTTP e sem relogio real: o que esta sendo verificado aqui
/// sao as invariantes, e nao a persistencia delas.
/// </summary>
public class RegraTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Agora = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Depois = Agora.AddMinutes(5);

    private static ConfiguracaoDeRegra Velocidade(int maximo = 3, int janela = 10) =>
        new ConfiguracaoDeVelocidade(maximo, janela);

    private static Regra Nova(string nome = "Velocidade por cliente", int pontos = 35) =>
        Regra.Criar(Tenant, TipoDeRegra.VelocidadePorCliente, nome, Velocidade(), pontos, Agora);

    // -----------------------------------------------------------------------
    // Nascimento
    // -----------------------------------------------------------------------

    [Fact]
    public void Regra_nasce_como_rascunho_e_nao_vale_para_ninguem()
    {
        var regra = Nova();

        // Existe, esta ativa, e ainda assim nao participa de perfil nenhum:
        // sem versao publicada nao ha o que o motor execute. A tela precisa
        // dizer as duas coisas, senao o Supervisor acha que configurou o
        // motor quando so escreveu um rascunho.
        Assert.True(regra.Ativa);
        Assert.True(regra.TemRascunho);
        Assert.False(regra.FoiPublicada);
        Assert.Equal(0, regra.NumeroDaUltimaVersao);
        Assert.Equal(35, regra.PontosEmRascunho);
    }

    [Fact]
    public void Regra_sem_organizacao_e_recusada()
    {
        // Uma regra sem tenant escaparia do filtro global e valeria para
        // todos ou para ninguem — as duas respostas erradas.
        Assert.Throws<ViolacaoDeInvariante>(() => Regra.Criar(
            Guid.Empty,
            TipoDeRegra.VelocidadePorCliente,
            "Qualquer",
            Velocidade(),
            10,
            Agora));
    }

    [Fact]
    public void Configuracao_de_outro_tipo_nao_serve_para_a_regra()
    {
        // O tipo e da identidade da regra. Aceitar uma configuracao de outro
        // tipo faria o motor procurar um avaliador que nao combina com o que
        // a regra diz ser.
        Assert.Throws<ViolacaoDeInvariante>(() => Regra.Criar(
            Tenant,
            TipoDeRegra.VelocidadePorCliente,
            "Mistura",
            new ConfiguracaoDeNovoDispositivo(3),
            10,
            Agora));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData("   ")]
    public void Nome_curto_demais_e_recusado(string nome) =>
        Assert.Throws<ViolacaoDeInvariante>(() => Nova(nome));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Peso_fora_da_faixa_e_recusado(int pontos)
    {
        // Zero seria uma regra que dispara e nao soma nada: apareceria na
        // explicacao como evidencia sem peso, e ninguem saberia dizer se foi
        // engano ou intencao.
        Assert.Throws<ViolacaoDeInvariante>(() => Nova(pontos: pontos));
    }

    [Fact]
    public void Configuracao_invalida_e_recusada_ja_no_rascunho()
    {
        // Validar so na publicacao faria o Supervisor descobrir o erro no
        // ultimo passo, depois de achar que o trabalho estava salvo.
        Assert.Throws<ViolacaoDeInvariante>(() => Regra.Criar(
            Tenant,
            TipoDeRegra.VelocidadePorCliente,
            "Janela impossivel",
            new ConfiguracaoDeVelocidade(MaximoDeTransacoes: 3, JanelaEmMinutos: 0),
            10,
            Agora));
    }

    // -----------------------------------------------------------------------
    // Publicacao
    // -----------------------------------------------------------------------

    [Fact]
    public void Publicar_congela_o_rascunho_em_versao_e_esvazia_o_rascunho()
    {
        var regra = Nova();

        var versao = regra.PublicarRascunho(ultimaPublicada: null, Depois);

        Assert.Equal(1, versao.Numero);
        Assert.Equal(35, versao.Pontos);
        Assert.Equal(Velocidade(), versao.Configuracao);
        Assert.Equal(regra.Id, versao.RegraId);

        // O rascunho sumiu: o que vale agora e a versao. Deixa-lo aberto
        // faria a tela mostrar "alteracoes pendentes" para sempre.
        Assert.False(regra.TemRascunho);
        Assert.True(regra.FoiPublicada);
        Assert.Equal(1, regra.NumeroDaUltimaVersao);
    }

    [Fact]
    public void Publicar_sem_rascunho_e_recusado()
    {
        var regra = Nova();

        regra.PublicarRascunho(null, Depois);

        Assert.Throws<ViolacaoDeInvariante>(() => regra.PublicarRascunho(null, Depois));
    }

    [Fact]
    public void Publicar_rascunho_igual_a_versao_publicada_e_recusado()
    {
        // Uma v2 identica a v1 faria o historico de versoes afirmar uma
        // mudanca que nao houve — e e esse historico que a Fase 9 vai usar
        // para comparar regras candidatas.
        var regra = Nova();
        var v1 = regra.PublicarRascunho(null, Agora);

        regra.SalvarRascunho("Velocidade por cliente", Velocidade(), 35, Depois);

        var erro = Assert.Throws<ViolacaoDeInvariante>(() => regra.PublicarRascunho(v1, Depois));

        Assert.Contains("igual a versao publicada", erro.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Publicar_com_uma_mudanca_so_de_peso_e_permitido()
    {
        var regra = Nova();
        var v1 = regra.PublicarRascunho(null, Agora);

        regra.SalvarRascunho("Velocidade por cliente", Velocidade(), pontos: 40, Depois);

        var v2 = regra.PublicarRascunho(v1, Depois);

        Assert.Equal(2, v2.Numero);
        Assert.Equal(40, v2.Pontos);
    }

    [Fact]
    public void Publicar_numera_as_versoes_em_sequencia()
    {
        var regra = Nova();

        var v1 = regra.PublicarRascunho(null, Agora);

        regra.SalvarRascunho("Velocidade por cliente", Velocidade(janela: 20), 35, Depois);
        var v2 = regra.PublicarRascunho(v1, Depois);

        regra.SalvarRascunho("Velocidade por cliente", Velocidade(janela: 30), 35, Depois);
        var v3 = regra.PublicarRascunho(v2, Depois);

        Assert.Equal([1, 2, 3], new[] { v1.Numero, v2.Numero, v3.Numero });

        // A versao antiga continua exatamente como foi publicada. Nao ha
        // metodo que a altere — e um teste de arquitetura confere que
        // continua assim.
        Assert.Equal(new ConfiguracaoDeVelocidade(3, 10), v1.Configuracao);
    }

    [Fact]
    public void Regra_desativada_nao_publica()
    {
        // Publicar uma versao de uma regra desligada criaria uma versao que
        // nao entra em perfil nenhum: o Supervisor acharia que fez alguma
        // coisa e o motor continuaria igual.
        var regra = Nova();

        regra.PublicarRascunho(null, Agora);
        regra.SalvarRascunho("Velocidade por cliente", Velocidade(janela: 20), 35, Depois);
        regra.Desativar(Depois);

        Assert.Throws<ViolacaoDeInvariante>(() => regra.PublicarRascunho(null, Depois));
    }

    // -----------------------------------------------------------------------
    // Rascunho
    // -----------------------------------------------------------------------

    [Fact]
    public void Salvar_rascunho_nao_muda_o_que_o_motor_executa()
    {
        var regra = Nova();
        var v1 = regra.PublicarRascunho(null, Agora);

        regra.SalvarRascunho("Velocidade agressiva", Velocidade(maximo: 1, janela: 60), 90, Depois);

        // A versao publicada continua a mesma. E essa distancia entre
        // "escrito" e "em vigor" que existe para a mudanca poder ser revista
        // antes de alcancar transacao de cliente.
        Assert.Equal(new ConfiguracaoDeVelocidade(3, 10), v1.Configuracao);
        Assert.Equal(35, v1.Pontos);
        Assert.Equal(1, regra.NumeroDaUltimaVersao);

        // O nome, sim, vale na hora: e rotulo, e nao comportamento.
        Assert.Equal("Velocidade agressiva", regra.Nome);
    }

    [Fact]
    public void Descartar_rascunho_devolve_a_regra_a_ultima_versao_publicada()
    {
        var regra = Nova();

        regra.PublicarRascunho(null, Agora);
        regra.SalvarRascunho("Velocidade por cliente", Velocidade(janela: 999), 35, Depois);

        Assert.True(regra.TemRascunho);

        regra.DescartarRascunho(Depois);

        Assert.False(regra.TemRascunho);
        Assert.Equal(1, regra.NumeroDaUltimaVersao);
    }

    [Fact]
    public void Descartar_o_rascunho_de_uma_regra_nunca_publicada_e_recusado()
    {
        // Ela ficaria viva no catalogo, sem configuracao nenhuma e impossivel
        // de publicar. Desativar e a saida honesta.
        var regra = Nova();

        var erro = Assert.Throws<ViolacaoDeInvariante>(() => regra.DescartarRascunho(Depois));

        Assert.Contains("nunca foi publicada", erro.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Descartar_sem_rascunho_e_recusado()
    {
        var regra = Nova();

        regra.PublicarRascunho(null, Agora);

        Assert.Throws<ViolacaoDeInvariante>(() => regra.DescartarRascunho(Depois));
    }

    // -----------------------------------------------------------------------
    // Ativacao
    // -----------------------------------------------------------------------

    [Fact]
    public void Desativar_e_reativar_nao_apagam_o_historico()
    {
        var regra = Nova();
        var v1 = regra.PublicarRascunho(null, Agora);

        regra.Desativar(Depois);
        Assert.False(regra.Ativa);

        regra.Reativar(Depois);
        Assert.True(regra.Ativa);

        // Desativar nao e excluir: as versoes publicadas continuam existindo,
        // e as avaliacoes que as usaram continuam explicaveis.
        Assert.Equal(1, v1.Numero);
        Assert.Equal(1, regra.NumeroDaUltimaVersao);
    }

    [Fact]
    public void Ligar_o_que_ja_esta_ligado_e_recusado()
    {
        // A recusa e o que impede a operacao de publicar uma versao de perfil
        // identica a anterior: sem mudanca real, nao ha o que suceder.
        var regra = Nova();

        Assert.Throws<ViolacaoDeInvariante>(() => regra.Reativar(Depois));

        regra.Desativar(Depois);

        Assert.Throws<ViolacaoDeInvariante>(() => regra.Desativar(Depois));
    }

    // -----------------------------------------------------------------------
    // Concorrencia administrativa
    // -----------------------------------------------------------------------

    [Fact]
    public void Toda_alteracao_sobe_a_versao_da_regra()
    {
        // E este numero que o cliente devolve em cada acao. Sem ele, dois
        // supervisores editando a mesma regra escreveriam um por cima do
        // outro e o ultimo venceria em silencio (ROADMAP 8.6).
        var regra = Nova();
        var inicial = regra.Versao;

        regra.SalvarRascunho("Outro nome", Velocidade(janela: 20), 35, Depois);
        var apos = regra.Versao;

        regra.PublicarRascunho(null, Depois);

        Assert.True(apos > inicial);
        Assert.True(regra.Versao > apos);
    }
}
