using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Investigacao;

/// <summary>
/// Uma investigacao humana.
///
/// **Caso nao e alerta** (`CLAUDE.md` secao 65). O alerta e produzido pela
/// maquina a partir de uma avaliacao; o caso e o trabalho que uma pessoa
/// conduz sobre um ou mais alertas. Ele tem responsavel, historico, notas e
/// uma conclusao — coisas que nenhuma maquina produz.
///
/// **O caso e a raiz do agregado.** Alertas entram por ele, notas nascem por
/// ele e cada acao deixa uma entrada na timeline **na mesma operacao**. Um
/// caminho que mudasse o estado sem passar por aqui produziria um caso cuja
/// historia nao bate com o proprio estado — e a timeline deixaria de ser
/// prova.
///
/// **Resolvido e imutavel.** Nao ha reabertura, nao ha nota depois da
/// conclusao e nao ha troca de responsavel. A razao e o que o caso alimenta: a
/// Fase 9 usa o resultado humano como verdade para comparar regras candidatas.
/// Um veredito que muda depois faria backtests antigos passarem a mentir sem
/// que ninguem percebesse. Se um dia a operacao precisar corrigir uma
/// conclusao, o caminho honesto e um caso novo — que a timeline registra — e
/// nao apagar o que ja foi decidido.
/// </summary>
public sealed class Caso
{
    public const int TamanhoMinimoDoTitulo = 3;
    public const int TamanhoMaximoDoTitulo = 120;

    private readonly List<EventoDoCaso> _novosEventos = [];
    private readonly List<NotaDoCaso> _novasNotas = [];

    private Caso(
        Guid id,
        Guid organizacaoId,
        string titulo,
        Guid abertoPorId,
        DateTimeOffset abertoEm)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        Titulo = titulo;
        Status = StatusDoCaso.Novo;
        AbertoPorId = abertoPorId;
        AbertoEm = abertoEm;
        AtualizadoEm = abertoEm;
        Versao = 1;
    }

    // Construtor usado pelo EF Core na materializacao.
    private Caso() => Titulo = string.Empty;

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public string Titulo { get; private set; }

    public StatusDoCaso Status { get; private set; }

    /// <summary>Quem esta com o caso. Nulo enquanto ninguem assumiu.</summary>
    public Guid? ResponsavelId { get; private set; }

    /// <summary>Preenchido somente na resolucao, e nunca mais alterado.</summary>
    public ResultadoDaInvestigacao? Resultado { get; private set; }

    /// <summary>
    /// Muda a cada alteracao, e e o que impede o lost update.
    ///
    /// Duas coisas dependem dela, e as duas fazem falta. O cliente envia a
    /// versao que leu, e a aplicacao recusa antes de tocar no banco — isso
    /// devolve um conflito claro no caso comum. E o banco a usa como token de
    /// concorrencia, entao duas requisicoes que passem juntas pela primeira
    /// checagem ainda assim nao se sobrescrevem: a segunda atualiza zero
    /// linhas e falha.
    ///
    /// Sem a segunda camada, dois analistas resolvendo o mesmo caso ao mesmo
    /// tempo gravariam resultados diferentes, e o ultimo venceria em silencio.
    /// </summary>
    public int Versao { get; private set; }

    public Guid AbertoPorId { get; private set; }

    public DateTimeOffset AbertoEm { get; private set; }

    public DateTimeOffset AtualizadoEm { get; private set; }

    public Guid? ResolvidoPorId { get; private set; }

    public DateTimeOffset? ResolvidoEm { get; private set; }

    /// <summary>
    /// Quantas entradas a timeline ja tem.
    ///
    /// E o que da ordem total a trilha. Horario nao serve: abrir um caso grava
    /// dois eventos no mesmo instante, e o UUIDv7 nao desempata porque sorteia
    /// os bits finais.
    /// </summary>
    public int TotalDeEventos { get; private set; }

    /// <summary>
    /// As entradas de timeline que ESTA operacao produziu.
    ///
    /// **Nao e a historia inteira, e isso e uma decisao.** A timeline e
    /// append-only e nenhuma regra de dominio precisa le-la: o caso so
    /// acrescenta. Carrega-la para gravar uma nota seria trazer centenas de
    /// linhas para nao consultar nenhuma delas.
    ///
    /// Quem persiste e o repositorio, que le esta lista depois de cada
    /// operacao. Deixar o EF descobrir estas entradas por navegacao produzia um
    /// defeito silencioso: com a chave gerada pelo dominio, ele concluia que a
    /// linha ja existia e emitia <c>UPDATE</c> em vez de <c>INSERT</c>.
    /// </summary>
    public IReadOnlyList<EventoDoCaso> NovosEventos => _novosEventos;

    /// <summary>As notas que esta operacao produziu. Mesma razao acima.</summary>
    public IReadOnlyList<NotaDoCaso> NovasNotas => _novasNotas;

    public bool EstaResolvido => Status == StatusDoCaso.Resolvido;

    // -----------------------------------------------------------------------
    // Abertura
    // -----------------------------------------------------------------------

    /// <summary>
    /// Abre um caso a partir de alertas.
    ///
    /// **Pelo menos um alerta, sempre** (ROADMAP 7.3). Um caso sem alerta nao
    /// tem transacao para investigar nem resultado para registrar — seria um
    /// ticket, e o `CLAUDE.md` secao 12 recusa transformar o produto nisso.
    ///
    /// Os alertas sao levados para o caso aqui dentro, e nao pelo chamador:
    /// deixar isso do lado de fora abriria a possibilidade de um caso existir
    /// citando um alerta que continuou aberto na fila de outra pessoa.
    /// </summary>
    public static Caso Abrir(
        Guid organizacaoId,
        string titulo,
        IReadOnlyCollection<Alerta> alertas,
        Guid autorId,
        string autorDescricao,
        DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(alertas);

        if (organizacaoId == Guid.Empty || autorId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Caso exige organizacao e autor.");
        }

        var nome = ValidarTitulo(titulo);

        if (alertas.Count == 0)
        {
            throw new ViolacaoDeInvariante(
                "Um caso nasce de pelo menos um alerta: sem alerta nao ha o que investigar.");
        }

        var caso = new Caso(Identificador.Novo(), organizacaoId, nome, autorId, agora);

        caso.RegistrarEvento(
            TipoDeEventoDoCaso.CasoAberto,
            autorId,
            autorDescricao,
            $"Caso aberto com {alertas.Count} alerta(s).",
            agora);

        foreach (var alerta in alertas)
        {
            caso.AssociarAlerta(alerta, autorId, autorDescricao, agora);
        }

        return caso;
    }

    /// <summary>
    /// Traz um alerta para o caso.
    ///
    /// Tres recusas, e cada uma fecha um buraco diferente: alerta de outro
    /// tenant (o dado de um cliente entrando na investigacao de outro), alerta
    /// que ja esta em algum caso (dois analistas concluindo coisas diferentes
    /// sobre a mesma transacao) e caso ja resolvido (mudar a base de um
    /// veredito depois de dado).
    /// </summary>
    public void AssociarAlerta(
        Alerta alerta,
        Guid autorId,
        string autorDescricao,
        DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(alerta);

        GarantirQueNaoEstaResolvido("associar alerta");

        if (alerta.OrganizacaoId != OrganizacaoId)
        {
            throw new ViolacaoDeInvariante(
                "Um alerta de outra organizacao nao pode entrar neste caso.");
        }

        if (alerta.CasoId is not null)
        {
            throw new ViolacaoDeInvariante(
                alerta.CasoId == Id
                    ? "Este alerta ja faz parte deste caso."
                    : "Este alerta ja faz parte de outro caso.");
        }

        alerta.LevarParaCaso(Id);

        RegistrarEvento(
            TipoDeEventoDoCaso.AlertaAssociado,
            autorId,
            autorDescricao,
            $"Alerta de prioridade {alerta.Prioridade} e score {alerta.Score} associado.",
            agora,
            alerta.Id);

        Tocar(agora);
    }

    // -----------------------------------------------------------------------
    // Ownership
    // -----------------------------------------------------------------------

    /// <summary>
    /// Alguem pega o caso para si.
    ///
    /// Assumir e sempre para **si mesmo**. Tirar o caso de outra pessoa e
    /// transferencia, e transferencia e ato de supervisao — a autorizacao
    /// disso vive na camada de aplicacao, onde o perfil e conhecido.
    /// </summary>
    public void Assumir(Guid usuarioId, string usuarioDescricao, DateTimeOffset agora)
    {
        GarantirQueNaoEstaResolvido("assumir");

        if (usuarioId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Assumir um caso exige um usuario.");
        }

        if (ResponsavelId == usuarioId)
        {
            throw new ViolacaoDeInvariante("Este caso ja e seu.");
        }

        if (ResponsavelId is not null)
        {
            throw new ViolacaoDeInvariante(
                "O caso ja tem responsavel. Tirar um caso de outra pessoa e uma transferencia.");
        }

        ResponsavelId = usuarioId;

        RegistrarEvento(
            TipoDeEventoDoCaso.Atribuido,
            usuarioId,
            usuarioDescricao,
            $"{usuarioDescricao} assumiu o caso.",
            agora,
            usuarioId);

        ComecarAnalise();
        Tocar(agora);
    }

    /// <summary>Passa o caso para outra pessoa.</summary>
    public void Transferir(
        Guid paraUsuarioId,
        string paraDescricao,
        Guid autorId,
        string autorDescricao,
        DateTimeOffset agora)
    {
        GarantirQueNaoEstaResolvido("transferir");

        if (paraUsuarioId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("A transferencia exige um destinatario.");
        }

        if (ResponsavelId == paraUsuarioId)
        {
            throw new ViolacaoDeInvariante("O caso ja pertence a esta pessoa.");
        }

        ResponsavelId = paraUsuarioId;

        RegistrarEvento(
            TipoDeEventoDoCaso.Transferido,
            autorId,
            autorDescricao,
            $"Caso transferido para {paraDescricao}.",
            agora,
            paraUsuarioId);

        ComecarAnalise();
        Tocar(agora);
    }

    // -----------------------------------------------------------------------
    // Notas
    // -----------------------------------------------------------------------

    public NotaDoCaso AdicionarNota(
        string conteudo,
        Guid autorId,
        string autorDescricao,
        DateTimeOffset agora)
    {
        GarantirQueNaoEstaResolvido("adicionar nota");

        var nota = NotaDoCaso.Escrever(OrganizacaoId, Id, autorId, autorDescricao, conteudo, agora);

        _novasNotas.Add(nota);

        // A nota entra na timeline pela referencia, e nao pelo conteudo: a
        // timeline diz o que aconteceu e quando; o texto vive na nota, que e
        // onde ele pode ser lido inteiro.
        RegistrarEvento(
            TipoDeEventoDoCaso.NotaAdicionada,
            autorId,
            autorDescricao,
            "Nota de investigacao adicionada.",
            agora,
            nota.Id);

        Tocar(agora);

        return nota;
    }

    // -----------------------------------------------------------------------
    // Resolucao
    // -----------------------------------------------------------------------

    /// <summary>
    /// Conclui a investigacao e transforma o veredito em dado operacional.
    ///
    /// **So de `EmAnalise`.** Resolver um caso que ninguem assumiu significaria
    /// concluir sem investigar, e o resultado alimentaria backtests como se
    /// fosse verdade apurada.
    ///
    /// Alem de fechar o caso, a resolucao encerra os alertas e grava o
    /// resultado por transacao (ROADMAP 7.9). E esse registro por transacao —
    /// e nao o caso — que a Fase 9 vai comparar contra regras candidatas.
    /// </summary>
    public IReadOnlyList<ResultadoDeInvestigacaoDaTransacao> Resolver(
        ResultadoDaInvestigacao resultado,
        IReadOnlyCollection<Alerta> alertasDoCaso,
        Guid autorId,
        string autorDescricao,
        DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(alertasDoCaso);

        GarantirQueNaoEstaResolvido("resolver");

        if (!Enum.IsDefined(resultado))
        {
            throw new ViolacaoDeInvariante($"Resultado de investigacao desconhecido: {resultado}.");
        }

        if (Status != StatusDoCaso.EmAnalise || ResponsavelId is null)
        {
            throw new ViolacaoDeInvariante(
                "So um caso em analise, com responsavel, pode ser resolvido.");
        }

        if (alertasDoCaso.Any(a => a.CasoId != Id))
        {
            throw new ViolacaoDeInvariante(
                "A resolucao recebeu um alerta que nao pertence a este caso.");
        }

        Status = StatusDoCaso.Resolvido;
        Resultado = resultado;
        ResolvidoPorId = autorId;
        ResolvidoEm = agora;

        var veredictos = new List<ResultadoDeInvestigacaoDaTransacao>(alertasDoCaso.Count);

        foreach (var alerta in alertasDoCaso)
        {
            alerta.Encerrar();

            veredictos.Add(ResultadoDeInvestigacaoDaTransacao.Registrar(
                OrganizacaoId,
                alerta.TransacaoId,
                Id,
                resultado,
                autorId,
                agora));
        }

        RegistrarEvento(
            TipoDeEventoDoCaso.Resolvido,
            autorId,
            autorDescricao,
            $"Caso resolvido como {resultado}, cobrindo {veredictos.Count} transacao(oes).",
            agora);

        Tocar(agora);

        return veredictos;
    }

    // -----------------------------------------------------------------------

    private void ComecarAnalise()
    {
        // `Novo -> EmAnalise` acontece por consequencia, e nao por um botao
        // separado: quem assume esta comecando a analisar. Um passo manual a
        // mais so criaria a chance de existir caso com responsavel e status
        // "ninguem pegou".
        if (Status == StatusDoCaso.Novo)
        {
            Status = StatusDoCaso.EmAnalise;
        }
    }

    private void RegistrarEvento(
        TipoDeEventoDoCaso tipo,
        Guid autorId,
        string autorDescricao,
        string descricao,
        DateTimeOffset agora,
        Guid? referenciaId = null) =>
        _novosEventos.Add(EventoDoCaso.Registrar(
            OrganizacaoId,
            Id,
            ++TotalDeEventos,
            tipo,
            autorId,
            autorDescricao,
            descricao,
            agora,
            referenciaId));

    private void Tocar(DateTimeOffset agora)
    {
        AtualizadoEm = agora;
        Versao++;
    }

    private void GarantirQueNaoEstaResolvido(string acao)
    {
        if (EstaResolvido)
        {
            throw new ViolacaoDeInvariante(
                $"Nao e possivel {acao}: o caso esta resolvido, e um caso resolvido nao muda.");
        }
    }

    private static string ValidarTitulo(string titulo)
    {
        if (!TextoDeInvestigacao.EhValido(
                titulo,
                TamanhoMinimoDoTitulo,
                TamanhoMaximoDoTitulo,
                "titulo",
                out var nome,
                out var erro))
        {
            throw new ViolacaoDeInvariante(erro);
        }

        return nome;
    }
}

/// <summary>
/// O veredito humano sobre uma transacao.
///
/// **Este e o produto duravel da investigacao.** O caso conta a historia; esta
/// tabela responde a pergunta que a Fase 9 vai fazer milhares de vezes: *para
/// esta transacao, o que a apuracao humana concluiu?*
///
/// Uma transacao tem no maximo um veredito, e a restricao unica garante isso.
/// O caminho ja torna a duplicata improvavel — um alerta por avaliacao, uma
/// avaliacao por transacao, um caso por alerta — mas improvavel nao e
/// impossivel, e um veredito duplicado faria a mesma transacao contar duas
/// vezes numa estatistica de backtest.
/// </summary>
public sealed class ResultadoDeInvestigacaoDaTransacao
{
    private ResultadoDeInvestigacaoDaTransacao(
        Guid id,
        Guid organizacaoId,
        Guid transacaoId,
        Guid casoId,
        ResultadoDaInvestigacao resultado,
        Guid registradoPorId,
        DateTimeOffset registradoEm)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        TransacaoId = transacaoId;
        CasoId = casoId;
        Resultado = resultado;
        RegistradoPorId = registradoPorId;
        RegistradoEm = registradoEm;
    }

    // Construtor usado pelo EF Core na materializacao.
    private ResultadoDeInvestigacaoDaTransacao()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public Guid TransacaoId { get; private set; }

    public Guid CasoId { get; private set; }

    public ResultadoDaInvestigacao Resultado { get; private set; }

    public Guid RegistradoPorId { get; private set; }

    public DateTimeOffset RegistradoEm { get; private set; }

    internal static ResultadoDeInvestigacaoDaTransacao Registrar(
        Guid organizacaoId,
        Guid transacaoId,
        Guid casoId,
        ResultadoDaInvestigacao resultado,
        Guid registradoPorId,
        DateTimeOffset agora)
    {
        if (organizacaoId == Guid.Empty || transacaoId == Guid.Empty || casoId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante(
                "Resultado de investigacao exige organizacao, transacao e caso.");
        }

        return new ResultadoDeInvestigacaoDaTransacao(
            Identificador.Novo(),
            organizacaoId,
            transacaoId,
            casoId,
            resultado,
            registradoPorId,
            agora);
    }
}
