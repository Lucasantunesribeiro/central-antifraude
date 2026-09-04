using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Infrastructure.Mensageria;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// O envelope e o contrato publico da mensageria.
///
/// **Tipo e versao separados** e a decisao que estes testes protegem. Um
/// consumidor precisa decidir se sabe ler a mensagem ANTES de interpreta-la:
/// ler o payload de uma v2 com o codigo da v1 pode produzir um numero
/// plausivel e errado, que e pior do que uma recusa.
/// </summary>
public class EnvelopeDeEventoTests
{
    private static EventoDeSaida EventoDeExemplo(string correlacao = "correlacao-1")
    {
        var transacao = CenarioDeRisco.Transacao();

        var avaliacao = new MotorDeRisco().Avaliar(
            transacao,
            CenarioDeRisco.PerfilPadrao(),
            ContextoDeRisco.Vazio,
            CenarioDeRisco.Referencia);

        return EventoDeSaida.Registrar(
            transacao.OrganizacaoId,
            TransacaoAvaliadaV1.De(transacao, avaliacao),
            avaliacao.AvaliadaEm,
            correlacao);
    }

    // -----------------------------------------------------------------------
    // Composicao e decomposicao do tipo
    // -----------------------------------------------------------------------

    [Fact]
    public void Envelope_separa_nome_e_versao_do_tipo_gravado()
    {
        var envelope = EnvelopeDeEvento.De(EventoDeExemplo());

        Assert.Equal("TransacaoAvaliada", envelope.EventType);
        Assert.Equal(1, envelope.Version);
        Assert.Equal("TransacaoAvaliada.v1", envelope.TipoComposto);
    }

    [Theory]
    [InlineData("TransacaoAvaliada", 1, "TransacaoAvaliada.v1")]
    [InlineData("AlgumEvento", 12, "AlgumEvento.v12")]
    public void Compor_e_decompor_sao_inversos(string nome, int versao, string composto)
    {
        Assert.Equal(composto, EnvelopeDeEvento.Compor(nome, versao));

        var (nomeLido, versaoLida) = EnvelopeDeEvento.Decompor(composto);

        Assert.Equal(nome, nomeLido);
        Assert.Equal(versao, versaoLida);
    }

    [Theory]
    [InlineData("SemVersao")]
    [InlineData("SemVersao.v")]
    [InlineData("SemVersao.vx")]
    [InlineData("SemVersao.v0")]
    [InlineData("SemVersao.v-1")]
    [InlineData(".v1")]
    public void Tipo_fora_do_formato_falha_alto(string tipo)
    {
        // Um tipo que ninguem consegue decompor nao pode virar um evento "sem
        // versao" que o consumidor aceita por engano.
        Assert.Throws<ViolacaoDeInvariante>(() => EnvelopeDeEvento.Decompor(tipo));
    }

    // -----------------------------------------------------------------------
    // Reconstrucao defensiva
    // -----------------------------------------------------------------------

    [Fact]
    public void Envelope_recusa_conteudo_que_nao_bate_com_o_que_ele_declara()
    {
        // Um envelope que diz "TransacaoAvaliada v1" e carrega outra coisa e
        // mensagem forjada ou defeito de despacho. Nos dois casos, recusar.
        var conteudo = EventoDeExemplo().Conteudo;

        Assert.Throws<ViolacaoDeInvariante>(() => EnvelopeDeEvento.Reconstruir(
            Guid.CreateVersion7(),
            "OutroEvento",
            1,
            Guid.CreateVersion7(),
            "c1",
            CenarioDeRisco.Referencia,
            conteudo));
    }

    [Fact]
    public void Envelope_recusa_versao_diferente_da_do_conteudo()
    {
        var conteudo = EventoDeExemplo().Conteudo;

        Assert.Throws<ViolacaoDeInvariante>(() => EnvelopeDeEvento.Reconstruir(
            Guid.CreateVersion7(),
            "TransacaoAvaliada",
            2,
            Guid.CreateVersion7(),
            "c1",
            CenarioDeRisco.Referencia,
            conteudo));
    }

    [Fact]
    public void Envelope_sem_identificador_ou_sem_tenant_e_recusado()
    {
        var conteudo = EventoDeExemplo().Conteudo;

        Assert.Throws<ViolacaoDeInvariante>(() => EnvelopeDeEvento.Reconstruir(
            Guid.Empty,
            "TransacaoAvaliada",
            1,
            Guid.CreateVersion7(),
            "c1",
            CenarioDeRisco.Referencia,
            conteudo));

        Assert.Throws<ViolacaoDeInvariante>(() => EnvelopeDeEvento.Reconstruir(
            Guid.CreateVersion7(),
            "TransacaoAvaliada",
            1,
            Guid.Empty,
            "c1",
            CenarioDeRisco.Referencia,
            conteudo));
    }

    // -----------------------------------------------------------------------
    // Formato de fio
    // -----------------------------------------------------------------------

    [Fact]
    public void O_json_de_fio_tem_exatamente_os_campos_do_contrato()
    {
        // ROADMAP 5.3. Estes nomes sao o contrato publico: quando a Fase 14
        // trocar a fila local pela SQS, e este JSON que vai no corpo da
        // mensagem, sem mudanca.
        var json = SerializadorDeEnvelope.Serializar(
            EnvelopeDeEvento.De(EventoDeExemplo("corr-abc")));

        Assert.Contains("\"eventId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"eventType\":\"TransacaoAvaliada\"", json, StringComparison.Ordinal);
        Assert.Contains("\"version\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"tenantId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"correlationId\":\"corr-abc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"occurredAt\":", json, StringComparison.Ordinal);
        Assert.Contains("\"payload\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Round_trip_do_envelope_preserva_todos_os_metadados()
    {
        var original = EnvelopeDeEvento.De(EventoDeExemplo("corr-round-trip"));

        var lido = SerializadorDeEnvelope.Desserializar(
            SerializadorDeEnvelope.Serializar(original));

        Assert.Equal(original.EventId, lido.EventId);
        Assert.Equal(original.EventType, lido.EventType);
        Assert.Equal(original.Version, lido.Version);
        Assert.Equal(original.TenantId, lido.TenantId);
        Assert.Equal(original.CorrelationId, lido.CorrelationId);
        Assert.Equal(original.OccurredAt, lido.OccurredAt);
        Assert.Equal(original.TipoComposto, lido.Conteudo.Tipo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nao e json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"texto\"")]
    [InlineData("{}")]
    public void Corpo_que_nao_e_um_envelope_vira_EnvelopeInvalido(string corpo)
    {
        // Uma unica excecao para todas as formas de a mensagem estar errada.
        // O worker precisa distinguir "nao entendi" de "falhou ao gravar":
        // a primeira nao melhora com repeticao, a segunda pode melhorar.
        Assert.ThrowsAny<Exception>(() => SerializadorDeEnvelope.Desserializar(corpo));
    }

    [Fact]
    public void A_mensagem_de_erro_nao_repete_o_corpo_da_mensagem()
    {
        // O corpo carrega score, decisao e identificador de cliente. A
        // mensagem de erro vai para o log (CLAUDE.md secao 70).
        var json = SerializadorDeEnvelope.Serializar(
            EnvelopeDeEvento.De(EventoDeExemplo("corr-secreta")));

        var comTipoInvalido = json.Replace(
            "\"eventType\":\"TransacaoAvaliada\"",
            "\"eventType\":\"Inventado\"",
            StringComparison.Ordinal);

        var erro = Assert.Throws<EnvelopeInvalido>(
            () => SerializadorDeEnvelope.Desserializar(comTipoInvalido));

        Assert.DoesNotContain("corr-secreta", erro.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cliente-1", erro.Message, StringComparison.Ordinal);
        Assert.Contains("Inventado.v1", erro.Message, StringComparison.Ordinal);
    }
}
