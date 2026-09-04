using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Infrastructure.Persistencia.Configuracoes;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// O contrato do evento e o que o consumidor da Fase 5 vai depender.
///
/// Duas coisas precisam ser verdade e continuar verdadeiras: o identificador
/// do evento e sorteado UMA vez (e o que faz a Inbox funcionar), e o conteudo
/// carrega o minimo necessario — nunca dado sensivel que o consumidor nao
/// precisa para agir.
/// </summary>
public class EventoDeSaidaTests
{
    private static AvaliacaoDeRisco AvaliacaoDe(Domain.Transacoes.Transacao transacao) =>
        new MotorDeRisco().Avaliar(
            transacao,
            CenarioDeRisco.PerfilPadrao(),
            ContextoDeRisco.Vazio,
            CenarioDeRisco.Referencia);

    [Fact]
    public void Evento_nasce_pendente_e_com_identificador_proprio()
    {
        var transacao = CenarioDeRisco.Transacao();
        var avaliacao = AvaliacaoDe(transacao);

        var evento = EventoDeSaida.Registrar(
            transacao.OrganizacaoId,
            TransacaoAvaliadaV1.De(transacao, avaliacao),
            avaliacao.AvaliadaEm,
            "correlacao-1");

        Assert.NotEqual(Guid.Empty, evento.Id);
        Assert.Null(evento.PublicadoEm);
        Assert.Equal(0, evento.TentativasDePublicacao);
        Assert.Equal(TransacaoAvaliadaV1.NomeDoTipo, evento.Tipo);
        Assert.Equal(avaliacao.AvaliadaEm, evento.OcorridoEm);
    }

    [Fact]
    public void Marcar_publicado_registra_o_instante_e_conta_a_tentativa()
    {
        var transacao = CenarioDeRisco.Transacao();
        var evento = EventoDeSaida.Registrar(
            transacao.OrganizacaoId,
            TransacaoAvaliadaV1.De(transacao, AvaliacaoDe(transacao)),
            CenarioDeRisco.Referencia,
            "correlacao-1");

        evento.RegistrarTentativaFalha();
        evento.MarcarPublicado(CenarioDeRisco.Referencia.AddSeconds(5));

        Assert.Equal(CenarioDeRisco.Referencia.AddSeconds(5), evento.PublicadoEm);
        Assert.Equal(2, evento.TentativasDePublicacao);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evento_sem_correlacao_e_recusado(string? correlacao)
    {
        // Sem correlacao o evento vira orfa: quando o worker da Fase 5 falhar,
        // nao havera como ligar o efeito a requisicao que o originou.
        var transacao = CenarioDeRisco.Transacao();
        var conteudo = TransacaoAvaliadaV1.De(transacao, AvaliacaoDe(transacao));

        Assert.Throws<ViolacaoDeInvariante>(() => EventoDeSaida.Registrar(
            transacao.OrganizacaoId,
            conteudo,
            CenarioDeRisco.Referencia,
            correlacao!));
    }

    [Fact]
    public void Evento_sem_organizacao_e_recusado()
    {
        var transacao = CenarioDeRisco.Transacao();
        var conteudo = TransacaoAvaliadaV1.De(transacao, AvaliacaoDe(transacao));

        Assert.Throws<ViolacaoDeInvariante>(() => EventoDeSaida.Registrar(
            Guid.Empty,
            conteudo,
            CenarioDeRisco.Referencia,
            "correlacao-1"));
    }

    [Fact]
    public void Conteudo_recusa_juntar_transacao_e_avaliacao_de_transacoes_diferentes()
    {
        // Um evento que descrevesse a decisao de uma transacao com os dados de
        // outra seria pior do que nenhum evento: o consumidor agiria sobre a
        // errada, e a trilha nao denunciaria nada.
        var uma = CenarioDeRisco.Transacao();
        var outra = CenarioDeRisco.Transacao();

        Assert.Throws<ViolacaoDeInvariante>(
            () => TransacaoAvaliadaV1.De(outra, AvaliacaoDe(uma)));
    }

    [Fact]
    public void Conteudo_nao_carrega_instrumento_dispositivo_nem_ip()
    {
        // Minimizacao (CLAUDE.md secao 56). Verificado no JSON gravado, e nao
        // so na forma do record: e o JSON que atravessa a fronteira.
        var transacao = CenarioDeRisco.Transacao(dispositivo: "disp-secreto");
        var conteudo = TransacaoAvaliadaV1.De(transacao, AvaliacaoDe(transacao));

        var json = SerializadorDeConteudoDeEvento.Serializar(conteudo);

        Assert.DoesNotContain("disp-secreto", json, StringComparison.Ordinal);
        Assert.DoesNotContain("pi_demo_1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("fingerprint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("instrumento", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Round_trip_do_conteudo_preserva_decisao_score_e_sinais()
    {
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(minutosAntes: 1),
            CenarioDeRisco.Anterior(minutosAntes: 2),
            CenarioDeRisco.Anterior(minutosAntes: 3));

        var transacao = CenarioDeRisco.Transacao(dispositivo: "disp-novo", pais: "RU");

        var avaliacao = new MotorDeRisco().Avaliar(
            transacao,
            CenarioDeRisco.PerfilPadrao(),
            contexto,
            CenarioDeRisco.Referencia);

        var original = TransacaoAvaliadaV1.De(transacao, avaliacao);

        var json = SerializadorDeConteudoDeEvento.Serializar(original);
        var reconstruido = Assert.IsType<TransacaoAvaliadaV1>(
            SerializadorDeConteudoDeEvento.Desserializar(original.Tipo, json));

        // Campo a campo, e nao `Assert.Equal(original, reconstruido)`: o
        // record compara a lista de sinais por REFERENCIA, entao a igualdade
        // pronta diria "diferente" mesmo com todo o conteudo igual.
        Assert.Equal(original.TransacaoId, reconstruido.TransacaoId);
        Assert.Equal(original.IdentificadorExterno, reconstruido.IdentificadorExterno);
        Assert.Equal(original.ClienteExternoId, reconstruido.ClienteExternoId);
        Assert.Equal(original.Valor, reconstruido.Valor);
        Assert.Equal(original.Moeda, reconstruido.Moeda);
        Assert.Equal(original.OcorridaEm, reconstruido.OcorridaEm);
        Assert.Equal(original.RecebidaEm, reconstruido.RecebidaEm);
        Assert.Equal(original.AvaliacaoId, reconstruido.AvaliacaoId);
        Assert.Equal(original.Score, reconstruido.Score);
        Assert.Equal(original.Decisao, reconstruido.Decisao);
        Assert.Equal(original.AvaliadaEm, reconstruido.AvaliadaEm);
        Assert.Equal(original.VersaoDePerfilId, reconstruido.VersaoDePerfilId);
        Assert.Equal(original.NumeroDaVersaoDePerfil, reconstruido.NumeroDaVersaoDePerfil);
        Assert.Equal(original.Sinais, reconstruido.Sinais);

        // A decisao volta como enum, e nao como texto solto: um consumidor
        // que receba "Bloquear" precisa receber o valor do dominio.
        Assert.NotEmpty(reconstruido.Sinais);
    }

    [Fact]
    public void Tipo_de_evento_desconhecido_falha_alto()
    {
        // O catalogo de eventos e fechado e versionado (CLAUDE.md secao 41).
        // Um tipo que ninguem declarou nao pode virar um objeto silencioso.
        Assert.Throws<InvalidOperationException>(
            () => SerializadorDeConteudoDeEvento.Desserializar("EventoInventado.v1", "{}"));
    }

    [Fact]
    public void Json_gravado_carrega_o_nome_versionado_do_tipo()
    {
        var transacao = CenarioDeRisco.Transacao();
        var conteudo = TransacaoAvaliadaV1.De(transacao, AvaliacaoDe(transacao));

        var json = SerializadorDeConteudoDeEvento.Serializar(conteudo);

        Assert.Contains("\"tipo\":\"TransacaoAvaliada.v1\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Sinais_do_evento_seguem_a_mesma_ordem_estavel_da_avaliacao()
    {
        // Dois consumidores comparando dois eventos precisam ver a mesma
        // ordem, senao "o que mudou entre estes dois eventos" vira ruido.
        var contexto = CenarioDeRisco.Contexto(
            CenarioDeRisco.Anterior(minutosAntes: 60 * 24),
            CenarioDeRisco.Anterior(minutosAntes: 60 * 48),
            CenarioDeRisco.Anterior(minutosAntes: 60 * 72));

        var transacao = CenarioDeRisco.Transacao(dispositivo: "disp-novo", pais: "RU");

        var avaliacao = new MotorDeRisco().Avaliar(
            transacao,
            CenarioDeRisco.PerfilPadrao(),
            contexto,
            CenarioDeRisco.Referencia);

        var conteudo = TransacaoAvaliadaV1.De(transacao, avaliacao);

        Assert.Equal(
            conteudo.Sinais.Select(s => s.Pontos).ToList(),
            conteudo.Sinais.Select(s => s.Pontos).OrderByDescending(p => p).ToList());

        Assert.Equal(avaliacao.Score, conteudo.Score);
        Assert.Equal(avaliacao.Decisao, conteudo.Decisao);
    }

    [Fact]
    public void Identificador_do_evento_nao_muda_entre_tentativas_de_publicacao()
    {
        // O eventId e o que o consumidor grava na Inbox para reconhecer a
        // mesma mensagem entregue duas vezes. Regenera-lo a cada tentativa
        // tornaria a Inbox inutil - e a entrega e at-least-once por desenho.
        var transacao = CenarioDeRisco.Transacao();
        var evento = EventoDeSaida.Registrar(
            transacao.OrganizacaoId,
            TransacaoAvaliadaV1.De(transacao, AvaliacaoDe(transacao)),
            CenarioDeRisco.Referencia,
            "correlacao-1");

        var idOriginal = evento.Id;

        evento.RegistrarTentativaFalha();
        evento.RegistrarTentativaFalha();
        evento.MarcarPublicado(CenarioDeRisco.Referencia);

        Assert.Equal(idOriginal, evento.Id);
    }

    [Fact]
    public void Identificador_do_evento_usa_o_formato_adotado_pelo_projeto()
    {
        // UUIDv7, como todo identificador interno (ADR 0002). Importa aqui
        // porque a Outbox e lida em ordem: um identificador aleatorio faria a
        // leitura das pendentes saltar pelo indice em vez de percorre-lo.
        var transacao = CenarioDeRisco.Transacao();
        var conteudo = TransacaoAvaliadaV1.De(transacao, AvaliacaoDe(transacao));

        var evento = EventoDeSaida.Registrar(
            transacao.OrganizacaoId, conteudo, CenarioDeRisco.Referencia, "c1");

        Assert.True(Identificador.EhDoFormatoAdotado(evento.Id));
    }
}
