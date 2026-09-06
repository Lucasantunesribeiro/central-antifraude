using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.Domain.Alertas;

/// <summary>
/// Quanto este alerta corre na frente dos outros na fila do analista.
///
/// Sao dois niveis porque a politica da Fase 6 tem dois desfechos: `Revisar`
/// pede olho humano, `Bloquear` pede olho humano com pressa. Inventar
/// `Baixa` e `Critica` agora seria criar vocabulario que nada produz e nada
/// consome — e uma tela de filtro com duas opcoes mortas.
/// </summary>
public enum PrioridadeDeAlerta
{
    /// <summary>Decisao `Revisar`: vale olhar, sem urgencia declarada.</summary>
    Media = 1,

    /// <summary>Decisao `Bloquear`: o motor recomendou nao prosseguir.</summary>
    Alta = 2,
}

/// <summary>
/// Situacao do alerta na operacao.
///
/// Nasceu com um valor so na Fase 6 e ganhou os outros dois na Fase 7, quando
/// passou a existir um caso para o qual o alerta pode ser levado — que e o que
/// o `CLAUDE.md` secao 12 pede: estado novo so com necessidade operacional
/// clara.
///
/// Os tres respondem a mesma pergunta do ponto de vista de quem trabalha a
/// fila: **isto ainda e trabalho meu?**
///
/// O alerta nunca muda de estado sozinho. Quem o move e sempre o caso, e cada
/// movimento fica na timeline dele.
/// </summary>
public enum StatusDoAlerta
{
    /// <summary>Na fila, sem ninguem investigando. E o que o analista pega.</summary>
    Aberto = 1,

    /// <summary>Levado para uma investigacao que ainda esta aberta.</summary>
    EmCaso = 2,

    /// <summary>O caso que o continha foi resolvido. Saiu da fila de trabalho.</summary>
    Encerrado = 3,
}

/// <summary>
/// Um resultado operacional do sistema: esta avaliacao merece olho humano.
///
/// **Alerta nao e caso** (`CLAUDE.md` secao 65). O alerta e produzido pela
/// maquina a partir de uma avaliacao; o caso e a investigacao que uma pessoa
/// conduz, e chega na Fase 7. Confundir os dois transformaria o produto em um
/// sistema de tickets com nome de antifraude.
///
/// **Por que o alerta copia score e decisao.** Ele poderia buscar os dois na
/// avaliacao a cada leitura, mas a fila operacional filtra e ordena por eles —
/// e filtrar por uma coluna de outra tabela obrigaria a juncao em toda
/// consulta da tela mais usada do produto. A copia e segura porque a avaliacao
/// e **congelada**: a Fase 4 provou, com teste, que ela nao muda depois de
/// gravada. Copiar um valor imutavel nao cria uma segunda fonte de verdade;
/// copiar um valor mutavel criaria.
///
/// **O que o alerta nao copia:** os sinais. Eles ja estao na avaliacao, com
/// explicacao e versao de regra, e sao carregados em lote para a pagina
/// inteira. Duplica-los aqui seria denormalizacao sem pergunta que a
/// justifique.
/// </summary>
public sealed class Alerta
{
    private Alerta(
        Guid id,
        Guid organizacaoId,
        Guid avaliacaoId,
        Guid transacaoId,
        Decisao decisao,
        int score,
        PrioridadeDeAlerta prioridade,
        StatusDoAlerta status,
        int versaoDaPolitica,
        Guid eventoId,
        string idDeCorrelacao,
        DateTimeOffset avaliadaEm,
        DateTimeOffset criadoEm)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        AvaliacaoId = avaliacaoId;
        TransacaoId = transacaoId;
        Decisao = decisao;
        Score = score;
        Prioridade = prioridade;
        Status = status;
        VersaoDaPolitica = versaoDaPolitica;
        EventoId = eventoId;
        IdDeCorrelacao = idDeCorrelacao;
        AvaliadaEm = avaliadaEm;
        CriadoEm = criadoEm;
    }

    // Construtor usado pelo EF Core na materializacao.
    private Alerta() => IdDeCorrelacao = string.Empty;

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    /// <summary>
    /// A avaliacao que originou o alerta.
    ///
    /// Unica no banco. E a invariante propria do efeito que o `CLAUDE.md`
    /// secao 42 exige alem da Inbox: mesmo que a marca de "ja processei" falhe
    /// ou seja apagada, o banco continua recusando o segundo alerta da mesma
    /// avaliacao.
    /// </summary>
    public Guid AvaliacaoId { get; private set; }

    /// <summary>Para onde o analista navega a partir da fila.</summary>
    public Guid TransacaoId { get; private set; }

    /// <summary>
    /// A investigacao que levou este alerta, quando existe.
    ///
    /// **Um alerta pertence a no maximo um caso**, e a restricao do banco
    /// garante isso pela unicidade de <c>avaliacao_id</c> somada a esta coluna
    /// so ser preenchida uma vez. Dois casos sobre o mesmo alerta produziriam
    /// duas conclusoes humanas sobre a mesma transacao — e a Fase 9 nao teria
    /// como saber qual delas e a verdade.
    /// </summary>
    public Guid? CasoId { get; private set; }

    public Decisao Decisao { get; private set; }

    public int Score { get; private set; }

    public PrioridadeDeAlerta Prioridade { get; private set; }

    public StatusDoAlerta Status { get; private set; }

    /// <summary>
    /// Versao da politica que decidiu criar este alerta com esta prioridade.
    ///
    /// Guardada pelo mesmo motivo que a avaliacao guarda a versao do perfil:
    /// para que "por que este alerta e Alta?" continue tendo resposta depois
    /// que a politica mudar.
    /// </summary>
    public int VersaoDaPolitica { get; private set; }

    /// <summary>
    /// O `eventId` do envelope que produziu o alerta (ROADMAP 6.3, origem do
    /// evento). Liga o alerta a linha da Inbox e a da Outbox.
    /// </summary>
    public Guid EventoId { get; private set; }

    /// <summary>
    /// Correlacao da requisicao HTTP que originou tudo isto.
    ///
    /// Gravada no efeito, e nao apenas no log, pela mesma razao da Inbox: o
    /// log some com a retencao e a pergunta "qual requisicao gerou este
    /// alerta?" continua valendo depois.
    /// </summary>
    public string IdDeCorrelacao { get; private set; }

    /// <summary>Quando o motor decidiu. Nao e quando o alerta apareceu.</summary>
    public DateTimeOffset AvaliadaEm { get; private set; }

    /// <summary>
    /// Quando o alerta entrou na fila.
    ///
    /// Diferente de <see cref="AvaliadaEm"/> de proposito: o caminho e
    /// assincrono, e a distancia entre os dois e a latencia real do backbone.
    /// Trata-los como iguais esconderia atraso de processamento.
    /// </summary>
    public DateTimeOffset CriadoEm { get; private set; }

    /// <summary>
    /// Leva o alerta para uma investigacao.
    ///
    /// `internal` de proposito: quem chama e o <c>Caso</c>, no mesmo agregado
    /// e na mesma operacao que grava a timeline. Deixar isto publico abriria a
    /// possibilidade de um alerta mudar de estado sem que nenhuma investigacao
    /// registrasse por que.
    /// </summary>
    internal void LevarParaCaso(Guid casoId)
    {
        if (Status != StatusDoAlerta.Aberto || CasoId is not null)
        {
            throw new ViolacaoDeInvariante(
                "So um alerta aberto e sem caso pode ser levado para uma investigacao.");
        }

        CasoId = casoId;
        Status = StatusDoAlerta.EmCaso;
    }

    /// <summary>Tira o alerta da fila de trabalho quando o caso e resolvido.</summary>
    internal void Encerrar()
    {
        if (Status != StatusDoAlerta.EmCaso)
        {
            throw new ViolacaoDeInvariante(
                "So um alerta em investigacao pode ser encerrado.");
        }

        Status = StatusDoAlerta.Encerrado;
    }

    internal static Alerta Registrar(
        Guid organizacaoId,
        Guid avaliacaoId,
        Guid transacaoId,
        Decisao decisao,
        int score,
        PrioridadeDeAlerta prioridade,
        int versaoDaPolitica,
        Guid eventoId,
        string idDeCorrelacao,
        DateTimeOffset avaliadaEm,
        DateTimeOffset criadoEm)
    {
        if (organizacaoId == Guid.Empty || avaliacaoId == Guid.Empty || transacaoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante(
                "Alerta exige organizacao, avaliacao e transacao.");
        }

        if (eventoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante(
                "Alerta exige o evento de origem: sem ele nao ha como refazer o caminho ate a requisicao.");
        }

        if (score is < VersaoDePerfilDeRisco.ScoreMinimo or > VersaoDePerfilDeRisco.ScoreMaximo)
        {
            throw new ViolacaoDeInvariante(
                $"Score fora da faixa {VersaoDePerfilDeRisco.ScoreMinimo}..{VersaoDePerfilDeRisco.ScoreMaximo}: {score}.");
        }

        if (!Enum.IsDefined(decisao) || !Enum.IsDefined(prioridade))
        {
            throw new ViolacaoDeInvariante("Decisao ou prioridade desconhecida.");
        }

        return new Alerta(
            Identificador.Novo(),
            organizacaoId,
            avaliacaoId,
            transacaoId,
            decisao,
            score,
            prioridade,
            StatusDoAlerta.Aberto,
            versaoDaPolitica,
            eventoId,
            idDeCorrelacao ?? string.Empty,
            avaliadaEm,
            criadoEm);
    }
}
