namespace CentralAntifraude.Application.Observabilidade;

/// <summary>
/// O catalogo de metricas do produto, em um lugar so.
///
/// **Por que um catalogo, e nao nomes soltos no ponto de uso.** Ate a Fase 11 o
/// projeto tinha tres instrumentos, criados onde foram precisos, com o nome do
/// medidor escrito a mao em cada classe. Isso funciona ate a primeira pergunta
/// operacional de verdade — "quais metricas existem?" — cuja resposta so podia
/// ser obtida lendo o codigo inteiro. Um exportador (Fase 14) precisa dessa
/// lista, e um teste que verifique disciplina de cardinalidade tambem.
///
/// **A lista de dimensoes e uma permissao, e nao uma proibicao.** Uma lista de
/// proibidas envelhece mal: ela cobre o que alguem lembrou de proibir, e a
/// dimensao que explode a cardinalidade e sempre a que ninguem imaginou. Com
/// permissao explicita, uma metrica nova que carregue uma dimensao nao prevista
/// quebra o teste — e quem a criou precisa declarar por que aquela dimensao tem
/// poucos valores possiveis. O risco que isto fecha nao e uma metrica cara que
/// existe hoje; e a proxima (`CLAUDE.md` secao 71).
/// </summary>
public static class Telemetria
{
    /// <summary>Prefixo comum a todos os medidores do produto.</summary>
    public const string Prefixo = "CentralAntifraude.";

    /// <summary>Requisicoes HTTP.</summary>
    public const string MedidorDaApi = Prefixo + "Api";

    /// <summary>Avaliacao de risco: o caminho critico.</summary>
    public const string MedidorDeRisco = Prefixo + "Risco";

    /// <summary>Outbox, filas e workers.</summary>
    public const string MedidorDeMensageria = Prefixo + "Mensageria";

    /// <summary>Efeito operacional dos eventos.</summary>
    public const string MedidorDeAlertas = Prefixo + "Alertas";

    /// <summary>Disputa no isolamento forte.</summary>
    public const string MedidorDeConcorrencia = Prefixo + "Concorrencia";

    /// <summary>Todos os medidores, para quem for coletar ou exportar.</summary>
    public static readonly IReadOnlyList<string> Medidores =
    [
        MedidorDaApi,
        MedidorDeRisco,
        MedidorDeMensageria,
        MedidorDeAlertas,
        MedidorDeConcorrencia,
    ];

    /// <summary>
    /// As unicas dimensoes que uma metrica deste produto pode carregar.
    ///
    /// Cada uma tem um conjunto pequeno e fechado de valores possiveis:
    ///
    /// | Dimensao | Valores | Teto |
    /// |---|---|---|
    /// | <c>decisao</c> | Permitir, Revisar, Bloquear | 3 |
    /// | <c>prioridade</c> | as prioridades de alerta | 2 |
    /// | <c>resultado</c> | desfecho de uma operacao | ~5 |
    /// | <c>operacao</c> | o PADRAO da rota, nunca o caminho | ~50 |
    /// | <c>fila</c> | operacional, backtests | 2 |
    /// | <c>laco</c> | os tres lacos de fundo | 3 |
    ///
    /// `operacao` merece atencao: o valor e <c>GET /api/casos/{id}</c>, e nunca
    /// <c>GET /api/casos/9f3c...</c>. O caminho concreto carrega identificador
    /// de recurso — seria uma serie de metrica por caso investigado, e um dado
    /// do tenant vazando para um sistema de metricas que nao tem tenant.
    /// </summary>
    public static readonly IReadOnlySet<string> DimensoesPermitidas =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "decisao",
            "prioridade",
            "resultado",
            "operacao",
            "fila",
            "laco",
        };

    /// <summary>Nomes dos instrumentos. Renomear e mudanca de contrato.</summary>
    public static class Instrumentos
    {
        // --- API ---------------------------------------------------------
        public const string RequisicoesTotal = "requisicoes_total";
        public const string RequisicaoDuracao = "requisicao_duracao_ms";

        // --- Risco -------------------------------------------------------
        public const string AvaliacoesTotal = "avaliacoes_total";
        public const string AvaliacaoDuracao = "avaliacao_duracao_ms";
        public const string DecisoesTotal = "decisoes_total";

        // --- Mensageria --------------------------------------------------
        public const string OutboxPublicados = "outbox_publicados_total";
        public const string OutboxFalhas = "outbox_falhas_total";
        public const string OutboxPendentes = "outbox_pendentes";
        public const string OutboxIdade = "outbox_idade_segundos";
        public const string MensagensConsumidas = "mensagens_consumidas_total";
        public const string MensagensMortas = "mensagens_mortas_total";
        public const string FilaPendentes = "fila_pendentes";
        public const string FilaMortas = "fila_mortas";
        public const string FalhasDeLaco = "laco_falhas_total";
        public const string BacktestDuracao = "backtest_duracao_ms";

        // --- Alertas -----------------------------------------------------
        public const string AlertasCriados = "alertas_criados";

        // --- Concorrencia ------------------------------------------------
        public const string Retentativas = "operacao_critica_retentativas";
        public const string TentativasEsgotadas = "operacao_critica_tentativas_esgotadas";
    }
}
