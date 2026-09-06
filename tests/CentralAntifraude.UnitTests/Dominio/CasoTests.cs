using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// O caso, sem banco e sem HTTP.
///
/// **O que estes testes protegem e a coerencia entre estado e historia.** Cada
/// acao muda o caso E deixa uma entrada na timeline, na mesma operacao. Um
/// caminho que mudasse o estado sem registrar produziria um caso cuja historia
/// nao explica o proprio estado — e a timeline deixaria de ser prova.
///
/// O outro eixo e a imutabilidade do resolvido. A Fase 9 vai usar o resultado
/// humano como verdade para comparar regras candidatas; um veredito que muda
/// depois faria backtests antigos passarem a mentir em silencio.
/// </summary>
public class CasoTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Analista = Guid.CreateVersion7();
    private static readonly Guid Supervisor = Guid.CreateVersion7();
    private static readonly DateTimeOffset Agora = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    // -----------------------------------------------------------------------
    // Abertura
    // -----------------------------------------------------------------------

    [Fact]
    public void Um_caso_nasce_de_alertas_e_a_timeline_conta_isso()
    {
        var alerta = AlertaDe(Tenant, Decisao.Revisar, 45);

        var caso = Caso.Abrir(Tenant, "Cartoes testados em sequencia", [alerta], Analista, "Ana", Agora);

        Assert.Equal(StatusDoCaso.Novo, caso.Status);
        Assert.Null(caso.ResponsavelId);
        Assert.Null(caso.Resultado);
        Assert.Equal(Analista, caso.AbertoPorId);

        // Dois eventos: a abertura e a associacao do alerta. A historia comeca
        // completa, e nao com um "caso criado" solto.
        //
        // `NovosEventos` e o que ESTA operacao produziu — num teste de unidade,
        // com o agregado vivo o tempo todo, isso coincide com a historia
        // inteira. Em producao quem guarda a historia e a tabela, e o
        // repositorio le a lista para grava-la.
        Assert.Equal(
            [TipoDeEventoDoCaso.CasoAberto, TipoDeEventoDoCaso.AlertaAssociado],
            caso.NovosEventos.Select(e => e.Tipo));

        Assert.Equal(caso.Id, alerta.CasoId);
        Assert.Equal(StatusDoAlerta.EmCaso, alerta.Status);
    }

    [Fact]
    public void Caso_sem_alerta_e_recusado()
    {
        // Um caso sem alerta nao tem transacao para investigar nem resultado
        // para registrar. Seria um ticket, e o produto recusa virar isso.
        Assert.Throws<ViolacaoDeInvariante>(
            () => Caso.Abrir(Tenant, "Vazio", [], Analista, "Ana", Agora));
    }

    [Fact]
    public void Alerta_de_outra_organizacao_nao_entra_no_caso()
    {
        // O ataque mais silencioso desta fase: o dado de um cliente entrando na
        // investigacao de outro, sem erro nenhum no caminho.
        var deOutroTenant = AlertaDe(Guid.CreateVersion7(), Decisao.Bloquear, 80);

        Assert.Throws<ViolacaoDeInvariante>(
            () => Caso.Abrir(Tenant, "Forjado", [deOutroTenant], Analista, "Ana", Agora));
    }

    [Fact]
    public void Alerta_que_ja_esta_em_um_caso_nao_entra_em_outro()
    {
        // Dois casos sobre o mesmo alerta produziriam duas conclusoes humanas
        // sobre a mesma transacao, e a Fase 9 nao teria como saber qual e a
        // verdade.
        var alerta = AlertaDe(Tenant, Decisao.Revisar, 45);

        Caso.Abrir(Tenant, "Primeiro caso", [alerta], Analista, "Ana", Agora);

        var excecao = Assert.Throws<ViolacaoDeInvariante>(
            () => Caso.Abrir(Tenant, "Segundo caso", [alerta], Analista, "Ana", Agora));

        Assert.Contains("outro caso", excecao.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<script>roubar()</script>")]
    [InlineData("Fraude <b>grave</b>")]
    public void Titulo_invalido_e_recusado(string titulo)
    {
        var alerta = AlertaDe(Tenant, Decisao.Revisar, 45);

        Assert.Throws<ViolacaoDeInvariante>(
            () => Caso.Abrir(Tenant, titulo, [alerta], Analista, "Ana", Agora));
    }

    [Fact]
    public void Titulo_com_comparacao_numerica_e_aceito()
    {
        // `valor < 100` e exatamente o tipo de coisa que se escreve
        // investigando fraude. Recusar todo `<` tornaria isso impossivel.
        var alerta = AlertaDe(Tenant, Decisao.Revisar, 45);

        var caso = Caso.Abrir(Tenant, "Compras com valor < 100 repetidas", [alerta], Analista, "Ana", Agora);

        Assert.Equal("Compras com valor < 100 repetidas", caso.Titulo);
    }

    // -----------------------------------------------------------------------
    // Ownership
    // -----------------------------------------------------------------------

    [Fact]
    public void Assumir_um_caso_novo_ja_o_poe_em_analise()
    {
        // Sem isto existiria caso com responsavel e status "ninguem pegou". Um
        // passo manual a mais so criaria essa janela.
        var caso = CasoAberto();
        var versaoAntes = caso.Versao;

        caso.Assumir(Analista, "Ana", Agora);

        Assert.Equal(StatusDoCaso.EmAnalise, caso.Status);
        Assert.Equal(Analista, caso.ResponsavelId);
        Assert.True(caso.Versao > versaoAntes);
        Assert.Contains(caso.NovosEventos, e => e.Tipo == TipoDeEventoDoCaso.Atribuido);
    }

    [Fact]
    public void Nao_da_para_assumir_um_caso_que_ja_tem_dono()
    {
        // Tirar o caso de outra pessoa e transferencia, e transferencia e ato
        // de supervisao.
        var caso = CasoAberto();
        caso.Assumir(Analista, "Ana", Agora);

        var excecao = Assert.Throws<ViolacaoDeInvariante>(
            () => caso.Assumir(Guid.CreateVersion7(), "Bruno", Agora));

        Assert.Contains("transferencia", excecao.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Assumir_duas_vezes_o_proprio_caso_e_recusado()
    {
        var caso = CasoAberto();
        caso.Assumir(Analista, "Ana", Agora);

        Assert.Throws<ViolacaoDeInvariante>(() => caso.Assumir(Analista, "Ana", Agora));
    }

    [Fact]
    public void Transferencia_troca_o_responsavel_e_fica_na_timeline()
    {
        var caso = CasoAberto();
        caso.Assumir(Analista, "Ana", Agora);

        var bruno = Guid.CreateVersion7();
        caso.Transferir(bruno, "Bruno", Supervisor, "Sofia", Agora.AddMinutes(1));

        Assert.Equal(bruno, caso.ResponsavelId);

        var evento = caso.NovosEventos[^1];

        Assert.Equal(TipoDeEventoDoCaso.Transferido, evento.Tipo);
        Assert.Equal(Supervisor, evento.AutorId);
        Assert.Equal(bruno, evento.ReferenciaId);
        Assert.Contains("Bruno", evento.Descricao, StringComparison.Ordinal);
    }

    [Fact]
    public void Transferir_um_caso_novo_tambem_o_poe_em_analise()
    {
        var caso = CasoAberto();

        caso.Transferir(Analista, "Ana", Supervisor, "Sofia", Agora);

        Assert.Equal(StatusDoCaso.EmAnalise, caso.Status);
    }

    // -----------------------------------------------------------------------
    // Notas
    // -----------------------------------------------------------------------

    [Fact]
    public void Nota_entra_na_lista_e_na_timeline_por_referencia()
    {
        var caso = CasoAberto();

        var nota = caso.AdicionarNota("Cliente confirmou a compra por telefone.", Analista, "Ana", Agora);

        Assert.Equal("Cliente confirmou a compra por telefone.", Assert.Single(caso.NovasNotas).Conteudo);

        var evento = caso.NovosEventos[^1];

        Assert.Equal(TipoDeEventoDoCaso.NotaAdicionada, evento.Tipo);
        Assert.Equal(nota.Id, evento.ReferenciaId);

        // O TEXTO da nota nao entra na timeline: ele vive na nota, que e onde
        // pode ser lido inteiro. Duplica-lo espalharia texto do tenant.
        Assert.DoesNotContain("telefone", evento.Descricao, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("olha isso <img src=x onerror=alert(1)>")]
    [InlineData("</b>")]
    public void Nota_com_marcacao_e_recusada_e_nao_limpa(string conteudo)
    {
        // Limpar mudaria o que a pessoa escreveu sem avisar, e uma nota de
        // investigacao alterada pelo sistema e pior do que uma recusada.
        var caso = CasoAberto();

        Assert.Throws<ViolacaoDeInvariante>(
            () => caso.AdicionarNota(conteudo, Analista, "Ana", Agora));

        Assert.Empty(caso.NovasNotas);
    }

    [Fact]
    public void Nota_com_texto_puro_que_parece_perigoso_mas_nao_e_passa_intacta()
    {
        var caso = CasoAberto();

        const string texto = "Cliente disse: valor < 100 e > 10. Marcou 3 < 5 tentativas.";

        var nota = caso.AdicionarNota(texto, Analista, "Ana", Agora);

        Assert.Equal(texto, nota.Conteudo);
    }

    [Fact]
    public void Nota_com_caractere_de_controle_e_recusada_mas_quebra_de_linha_passa()
    {
        var caso = CasoAberto();

        Assert.Throws<ViolacaoDeInvariante>(
            () => caso.AdicionarNota("linha nula", Analista, "Ana", Agora));

        var nota = caso.AdicionarNota("primeiro paragrafo\n\nsegundo", Analista, "Ana", Agora);

        Assert.Contains('\n', nota.Conteudo);
    }

    // -----------------------------------------------------------------------
    // Resolucao
    // -----------------------------------------------------------------------

    [Fact]
    public void Resolver_fecha_o_caso_encerra_os_alertas_e_grava_o_veredito()
    {
        var alerta = AlertaDe(Tenant, Decisao.Revisar, 45);
        var caso = Caso.Abrir(Tenant, "Viagem ao exterior", [alerta], Analista, "Ana", Agora);

        caso.Assumir(Analista, "Ana", Agora);

        // Falso positivo legitimo: o motor recomendou revisar, a pessoa
        // concluiu que era legitima. E o caso que o CLAUDE.md secao 94 exige
        // que o produto saiba demonstrar.
        var veredictos = caso.Resolver(
            ResultadoDaInvestigacao.Legitima,
            [alerta],
            Analista,
            "Ana",
            Agora.AddHours(1));

        Assert.Equal(StatusDoCaso.Resolvido, caso.Status);
        Assert.Equal(ResultadoDaInvestigacao.Legitima, caso.Resultado);
        Assert.Equal(Analista, caso.ResolvidoPorId);
        Assert.Equal(Agora.AddHours(1), caso.ResolvidoEm);

        Assert.Equal(StatusDoAlerta.Encerrado, alerta.Status);

        var veredito = Assert.Single(veredictos);

        Assert.Equal(alerta.TransacaoId, veredito.TransacaoId);
        Assert.Equal(caso.Id, veredito.CasoId);
        Assert.Equal(ResultadoDaInvestigacao.Legitima, veredito.Resultado);
    }

    [Fact]
    public void Um_caso_que_ninguem_assumiu_nao_pode_ser_resolvido()
    {
        // Concluir sem investigar produziria dado falso, e a Fase 9 usaria
        // isso como verdade apurada.
        var alerta = AlertaDe(Tenant, Decisao.Bloquear, 75);
        var caso = Caso.Abrir(Tenant, "Sem dono", [alerta], Analista, "Ana", Agora);

        Assert.Throws<ViolacaoDeInvariante>(() => caso.Resolver(
            ResultadoDaInvestigacao.FraudeConfirmada,
            [alerta],
            Analista,
            "Ana",
            Agora));
    }

    [Fact]
    public void Resultado_desconhecido_e_recusado()
    {
        var alerta = AlertaDe(Tenant, Decisao.Bloquear, 75);
        var caso = Caso.Abrir(Tenant, "Resultado invalido", [alerta], Analista, "Ana", Agora);
        caso.Assumir(Analista, "Ana", Agora);

        Assert.Throws<ViolacaoDeInvariante>(() => caso.Resolver(
            (ResultadoDaInvestigacao)99,
            [alerta],
            Analista,
            "Ana",
            Agora));
    }

    [Fact]
    public void Um_caso_com_varios_alertas_gera_um_veredito_por_transacao()
    {
        var primeiro = AlertaDe(Tenant, Decisao.Bloquear, 75);
        var segundo = AlertaDe(Tenant, Decisao.Revisar, 45);

        var caso = Caso.Abrir(Tenant, "Rajada do mesmo cliente", [primeiro, segundo], Analista, "Ana", Agora);
        caso.Assumir(Analista, "Ana", Agora);

        var veredictos = caso.Resolver(
            ResultadoDaInvestigacao.FraudeConfirmada,
            [primeiro, segundo],
            Analista,
            "Ana",
            Agora);

        Assert.Equal(2, veredictos.Count);

        Assert.Equal(
            new[] { primeiro.TransacaoId, segundo.TransacaoId }.Order(),
            veredictos.Select(v => v.TransacaoId).Order());
    }

    [Fact]
    public void A_resolucao_recusa_alerta_que_nao_e_do_caso()
    {
        // Um alerta de fora entrando na resolucao produziria veredito humano
        // sobre uma transacao que ninguem investigou.
        var doCaso = AlertaDe(Tenant, Decisao.Revisar, 45);
        var deFora = AlertaDe(Tenant, Decisao.Revisar, 45);

        var caso = Caso.Abrir(Tenant, "So um alerta", [doCaso], Analista, "Ana", Agora);
        caso.Assumir(Analista, "Ana", Agora);

        Assert.Throws<ViolacaoDeInvariante>(() => caso.Resolver(
            ResultadoDaInvestigacao.Legitima,
            [doCaso, deFora],
            Analista,
            "Ana",
            Agora));
    }

    // -----------------------------------------------------------------------
    // Imutabilidade do resolvido
    // -----------------------------------------------------------------------

    [Fact]
    public void Um_caso_resolvido_nao_muda_mais()
    {
        var alerta = AlertaDe(Tenant, Decisao.Revisar, 45);
        var caso = Caso.Abrir(Tenant, "Fechado", [alerta], Analista, "Ana", Agora);
        caso.Assumir(Analista, "Ana", Agora);
        caso.Resolver(ResultadoDaInvestigacao.Legitima, [alerta], Analista, "Ana", Agora);

        var versao = caso.Versao;
        var eventos = caso.NovosEventos.Count;

        Assert.Throws<ViolacaoDeInvariante>(
            () => caso.AdicionarNota("mais uma coisa", Analista, "Ana", Agora));

        Assert.Throws<ViolacaoDeInvariante>(
            () => caso.Assumir(Supervisor, "Sofia", Agora));

        Assert.Throws<ViolacaoDeInvariante>(
            () => caso.Transferir(Supervisor, "Sofia", Supervisor, "Sofia", Agora));

        Assert.Throws<ViolacaoDeInvariante>(
            () => caso.AssociarAlerta(AlertaDe(Tenant, Decisao.Revisar, 45), Analista, "Ana", Agora));

        Assert.Throws<ViolacaoDeInvariante>(() => caso.Resolver(
            ResultadoDaInvestigacao.FraudeConfirmada,
            [alerta],
            Analista,
            "Ana",
            Agora));

        // Nada foi tocado por nenhuma das cinco tentativas.
        Assert.Equal(versao, caso.Versao);
        Assert.Equal(eventos, caso.NovosEventos.Count);
        Assert.Equal(ResultadoDaInvestigacao.Legitima, caso.Resultado);
    }

    [Fact]
    public void Cada_alteracao_sobe_a_versao_uma_vez_so()
    {
        // A versao e o que o cliente devolve para provar que estava vendo o
        // estado atual. Se uma acao subisse duas casas, o cliente que acabou
        // de agir ficaria desatualizado sozinho.
        var alerta = AlertaDe(Tenant, Decisao.Revisar, 45);
        var caso = Caso.Abrir(Tenant, "Contagem", [alerta], Analista, "Ana", Agora);

        var depoisDaAbertura = caso.Versao;

        caso.Assumir(Analista, "Ana", Agora);
        Assert.Equal(depoisDaAbertura + 1, caso.Versao);

        caso.AdicionarNota("primeira nota", Analista, "Ana", Agora);
        Assert.Equal(depoisDaAbertura + 2, caso.Versao);
    }

    // -----------------------------------------------------------------------

    private static Caso CasoAberto() =>
        Caso.Abrir(Tenant, "Caso de teste", [AlertaDe(Tenant, Decisao.Revisar, 45)], Analista, "Ana", Agora);

    /// <summary>
    /// Um alerta legitimo, produzido pela politica — e nao por um construtor
    /// de teste. Assim o teste exercita o mesmo objeto que a producao cria.
    /// </summary>
    private static Alerta AlertaDe(Guid tenant, Decisao decisao, int score)
    {
        var conteudo = new TransacaoAvaliadaV1(
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

        return PoliticaDeAlertas.Avaliar(tenant, conteudo, Guid.CreateVersion7(), "corr", Agora)!;
    }
}
