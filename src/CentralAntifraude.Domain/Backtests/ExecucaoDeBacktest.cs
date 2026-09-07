using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Backtests;

/// <summary>
/// Uma execucao de backtest: aplicar um perfil candidato sobre transacoes
/// historicas sem tocar em nada.
///
/// **O que ela nao faz e a parte importante** (CLAUDE.md secao 25 e ROADMAP
/// 9.5). Um backtest nao altera avaliacao, nao cria alerta, nao cria caso, nao
/// modifica transacao e nao publica evento operacional. Tudo o que ele produz
/// e o proprio <see cref="Resultado"/> — um documento de simulacao, guardado
/// aqui e em lugar nenhum mais.
///
/// **Tudo o que decide o resultado esta congelado nesta linha**: o perfil
/// candidato, a versao do perfil vigente que serve de comparacao e a janela.
/// O worker nao le a configuracao atual das regras na hora de executar; se
/// lesse, um rascunho editado no meio do caminho mudaria silenciosamente a
/// pergunta que foi feita.
///
/// <code>
/// Pendente  →  Executando  →  Concluida
///                         ↘  Falhou
///     ↘         ↘            Cancelada
/// </code>
/// </summary>
public sealed class ExecucaoDeBacktest
{
    public const int TamanhoMaximoDaDescricao = 200;
    public const int TamanhoMaximoDoErro = 1_000;

    private ExecucaoDeBacktest(
        Guid id,
        Guid organizacaoId,
        string descricao,
        Guid? regraCandidataId,
        PerfilCandidato candidato,
        Guid versaoDePerfilVigenteId,
        int numeroDaVersaoDePerfilVigente,
        DateTimeOffset inicio,
        DateTimeOffset fim,
        Guid solicitadaPorId,
        string solicitadaPorDescricao,
        DateTimeOffset agora)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        Descricao = descricao;
        RegraCandidataId = regraCandidataId;
        Candidato = candidato;
        VersaoDePerfilVigenteId = versaoDePerfilVigenteId;
        NumeroDaVersaoDePerfilVigente = numeroDaVersaoDePerfilVigente;
        Inicio = inicio;
        Fim = fim;
        SolicitadaPorId = solicitadaPorId;
        SolicitadaPorDescricao = solicitadaPorDescricao;
        SolicitadaEm = agora;
        Status = StatusDoBacktest.Pendente;
        Versao = 1;
    }

    // Construtor usado pelo EF Core na materializacao.
    private ExecucaoDeBacktest()
    {
        Descricao = string.Empty;
        SolicitadaPorDescricao = string.Empty;
        Candidato = null!;
    }

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    /// <summary>Frase curta e deterministica sobre o que esta sendo simulado.</summary>
    public string Descricao { get; private set; }

    /// <summary>
    /// Regra cujo rascunho e o candidato. Nula quando a simulacao mexe apenas
    /// nos limiares do perfil.
    /// </summary>
    public Guid? RegraCandidataId { get; private set; }

    /// <summary>O perfil que seria publicado, congelado na solicitacao.</summary>
    public PerfilCandidato Candidato { get; private set; }

    /// <summary>
    /// Versao do perfil que serve de comparacao.
    ///
    /// Congelada tambem: o candidato e comparado contra o que valia quando a
    /// pergunta foi feita, e nao contra o que passar a valer enquanto a fila
    /// anda.
    /// </summary>
    public Guid VersaoDePerfilVigenteId { get; private set; }

    public int NumeroDaVersaoDePerfilVigente { get; private set; }

    /// <summary>Inicio da janela historica, sobre <c>OcorridaEm</c>.</summary>
    public DateTimeOffset Inicio { get; private set; }

    /// <summary>Fim da janela historica, sobre <c>OcorridaEm</c>. Inclusivo.</summary>
    public DateTimeOffset Fim { get; private set; }

    public StatusDoBacktest Status { get; private set; }

    /// <summary>Preenchido apenas quando <see cref="Status"/> e Concluida.</summary>
    public ResultadoDoBacktest? Resultado { get; private set; }

    /// <summary>Motivo da falha, em texto seguro para exibir.</summary>
    public string? MensagemDeErro { get; private set; }

    public Guid SolicitadaPorId { get; private set; }

    /// <summary>Nome de quem pediu, no momento do pedido. Copiado, nao juntado.</summary>
    public string SolicitadaPorDescricao { get; private set; }

    public DateTimeOffset SolicitadaEm { get; private set; }

    public DateTimeOffset? IniciadaEm { get; private set; }

    public DateTimeOffset? ConcluidaEm { get; private set; }

    /// <summary>
    /// Token de concorrencia.
    ///
    /// Faz dois trabalhos aqui. Impede que dois workers concluam a mesma
    /// execucao — o segundo escreve com a versao velha e perde. E arbitra o
    /// cancelamento: quem cancela durante a execucao sobe a versao, e o worker
    /// que tentar concluir depois e recusado pelo banco, sem precisar de
    /// nenhuma consulta de verificacao no meio do laco.
    /// </summary>
    public int Versao { get; private set; }

    /// <summary>Ja terminou, de qualquer forma.</summary>
    public bool EstaEncerrada =>
        Status is StatusDoBacktest.Concluida or StatusDoBacktest.Falhou or StatusDoBacktest.Cancelada;

    public static ExecucaoDeBacktest Solicitar(
        Guid organizacaoId,
        string descricao,
        Guid? regraCandidataId,
        PerfilCandidato candidato,
        Guid versaoDePerfilVigenteId,
        int numeroDaVersaoDePerfilVigente,
        DateTimeOffset inicio,
        DateTimeOffset fim,
        Guid solicitadaPorId,
        string solicitadaPorDescricao,
        DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(candidato);

        if (organizacaoId == Guid.Empty || solicitadaPorId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Execucao de backtest exige organizacao e solicitante.");
        }

        if (versaoDePerfilVigenteId == Guid.Empty || numeroDaVersaoDePerfilVigente < 1)
        {
            throw new ViolacaoDeInvariante(
                "Execucao de backtest exige a versao de perfil vigente como comparacao.");
        }

        // Janela invertida ou de duracao zero nao analisaria nada e ficaria
        // "concluida com sucesso" sobre zero transacoes — que e pior do que
        // recusar, porque parece uma resposta.
        if (fim <= inicio)
        {
            throw new ViolacaoDeInvariante("O fim da janela precisa ser depois do inicio.");
        }

        candidato.Validar();

        return new ExecucaoDeBacktest(
            Identificador.Novo(),
            organizacaoId,
            Encurtar(descricao, TamanhoMaximoDaDescricao),
            regraCandidataId,
            candidato,
            versaoDePerfilVigenteId,
            numeroDaVersaoDePerfilVigente,
            inicio,
            fim,
            solicitadaPorId,
            Encurtar(solicitadaPorDescricao, TamanhoMaximoDaDescricao),
            agora);
    }

    /// <summary>Um worker assumiu a execucao.</summary>
    public void Iniciar(DateTimeOffset agora)
    {
        Exigir(
            Status == StatusDoBacktest.Pendente,
            $"So uma execucao pendente pode ser iniciada. Esta esta {Status}.");

        Status = StatusDoBacktest.Executando;
        IniciadaEm = agora;

        Versao++;
    }

    /// <summary>
    /// Um worker reassume uma execucao que ficou presa em
    /// <see cref="StatusDoBacktest.Executando"/>.
    ///
    /// **Retomar e seguro porque um backtest nao tem efeito colateral.** Ele
    /// le transacoes e escreve um resultado no proprio registro; refazer o
    /// trabalho desperdica tempo e nada mais. Se houvesse efeito operacional,
    /// esta porta nao poderia existir — e e justamente por isso que o ROADMAP
    /// 9.5 proibe o efeito.
    ///
    /// Quem decide que ficou presa e quem chama, comparando
    /// <see cref="IniciadaEm"/> com o tempo maximo configurado.
    /// </summary>
    public void Retomar(DateTimeOffset agora)
    {
        Exigir(
            Status == StatusDoBacktest.Executando,
            $"So uma execucao em andamento pode ser retomada. Esta esta {Status}.");

        IniciadaEm = agora;

        Versao++;
    }

    public void Concluir(ResultadoDoBacktest resultado, DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(resultado);

        Exigir(
            Status == StatusDoBacktest.Executando,
            $"So uma execucao em andamento pode ser concluida. Esta esta {Status}.");

        Status = StatusDoBacktest.Concluida;
        Resultado = resultado;
        ConcluidaEm = agora;

        Versao++;
    }

    public void Falhar(string motivo, DateTimeOffset agora)
    {
        Exigir(
            Status is StatusDoBacktest.Pendente or StatusDoBacktest.Executando,
            $"So uma execucao pendente ou em andamento pode falhar. Esta esta {Status}.");

        Status = StatusDoBacktest.Falhou;
        MensagemDeErro = Encurtar(motivo, TamanhoMaximoDoErro);
        ConcluidaEm = agora;

        Versao++;
    }

    /// <summary>
    /// Cancela a execucao.
    ///
    /// Vale antes e durante. Cancelar uma execucao ja encerrada e recusado:
    /// reescrever um resultado que ja foi lido apagaria o que alguem viu.
    /// </summary>
    public void Cancelar(DateTimeOffset agora)
    {
        Exigir(
            Status is StatusDoBacktest.Pendente or StatusDoBacktest.Executando,
            Status == StatusDoBacktest.Cancelada
                ? "Esta execucao ja foi cancelada."
                : $"Uma execucao {Status} nao pode ser cancelada.");

        Status = StatusDoBacktest.Cancelada;
        ConcluidaEm = agora;

        Versao++;
    }

    private static void Exigir(bool condicao, string mensagem)
    {
        if (!condicao)
        {
            throw new ViolacaoDeInvariante(mensagem);
        }
    }

    private static string Encurtar(string? valor, int tamanhoMaximo)
    {
        var limpo = (valor ?? string.Empty).Trim();

        return limpo.Length <= tamanhoMaximo ? limpo : limpo[..tamanhoMaximo];
    }
}
