using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Domain.Investigacao;

/// <summary>
/// Onde o caso esta no fluxo de trabalho.
///
/// Tres estados, e o `CLAUDE.md` secao 12 e explicito sobre por que nao ha
/// mais: **nao transformar a aplicacao em um sistema generico de tickets**.
/// Cada estado aqui responde uma pergunta operacional real — ninguem pegou,
/// alguem esta olhando, alguem concluiu.
///
/// Nao existe `Reaberto`. Um caso resolvido e imutavel, e a razao esta em
/// <see cref="Caso"/>.
/// </summary>
public enum StatusDoCaso
{
    /// <summary>Aberto a partir de alertas, sem responsavel.</summary>
    Novo = 1,

    /// <summary>Alguem assumiu e esta investigando.</summary>
    EmAnalise = 2,

    /// <summary>Concluido, com resultado registrado. Imutavel.</summary>
    Resolvido = 3,
}

/// <summary>
/// A conclusao humana da investigacao.
///
/// **Nao e a decisao do motor** (`CLAUDE.md` secao 11). Uma transacao pode ter
/// recebido `Revisar` automaticamente e terminar como `Legitima` — isso e um
/// falso positivo legitimo, e nao um defeito. E exatamente a demonstracao que
/// o `CLAUDE.md` secao 94 exige do produto.
///
/// `Inconclusiva` existe porque forcar uma escolha binaria produziria dado
/// falso: um analista sem evidencia suficiente marcaria "legitima" por
/// eliminacao, e a Fase 9 usaria isso como verdade em backtests.
/// </summary>
public enum ResultadoDaInvestigacao
{
    FraudeConfirmada = 1,
    Legitima = 2,
    Inconclusiva = 3,
}

/// <summary>
/// O que aconteceu com o caso, e quando.
///
/// Vocabulario fechado pela mesma razao da auditoria (`CLAUDE.md` secao 67):
/// timeline com texto livre nao e consultavel nem comparavel.
/// </summary>
public enum TipoDeEventoDoCaso
{
    CasoAberto = 1,
    AlertaAssociado = 2,
    Atribuido = 3,
    Transferido = 4,
    NotaAdicionada = 5,
    Resolvido = 6,
}

/// <summary>
/// Uma entrada da linha do tempo do caso.
///
/// **Somente insercao** (`CLAUDE.md` secao 66 e ROADMAP 7.6). Nao ha metodo
/// para alterar nem apagar, e nao existe rota que faca isso. Uma timeline que
/// pode ser editada nao prova nada sobre a investigacao — seria a primeira
/// coisa a ser ajustada quando o resultado incomodasse alguem.
///
/// O nome do autor e guardado **por copia**, e nao por juncao: se a pessoa for
/// renomeada ou desativada depois, a timeline precisa continuar dizendo quem
/// era quando aquilo aconteceu. Mesma decisao da trilha de auditoria.
/// </summary>
public sealed class EventoDoCaso
{
    public const int TamanhoMaximoDaDescricao = 500;

    /// <summary>Mesmo teto do nome do usuario, que e o que se copia aqui.</summary>
    public const int TamanhoMaximoDoAutor = 200;

    private EventoDoCaso(
        Guid id,
        Guid organizacaoId,
        Guid casoId,
        int sequencia,
        TipoDeEventoDoCaso tipo,
        Guid autorId,
        string autorDescricao,
        string descricao,
        Guid? referenciaId,
        DateTimeOffset ocorridoEm)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        CasoId = casoId;
        Sequencia = sequencia;
        Tipo = tipo;
        AutorId = autorId;
        AutorDescricao = autorDescricao;
        Descricao = descricao;
        ReferenciaId = referenciaId;
        OcorridoEm = ocorridoEm;
    }

    // Construtor usado pelo EF Core na materializacao.
    private EventoDoCaso()
    {
        AutorDescricao = string.Empty;
        Descricao = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public Guid CasoId { get; private set; }

    /// <summary>
    /// Ordem de acontecimento dentro do caso, comecando em 1.
    ///
    /// **O horario nao basta para ordenar uma trilha.** Abrir um caso grava
    /// dois eventos no mesmo instante, e o identificador nao desempata: o
    /// UUIDv7 sorteia os bits finais, entao dois gerados no mesmo milissegundo
    /// nao saem em ordem. Sem a sequencia, a timeline mostraria "alerta
    /// associado" antes de "caso aberto" de vez em quando — e uma historia que
    /// muda de ordem entre duas leituras deixa de ser prova.
    /// </summary>
    public int Sequencia { get; private set; }

    public TipoDeEventoDoCaso Tipo { get; private set; }

    public Guid AutorId { get; private set; }

    /// <summary>Nome de quem agiu, no momento do fato.</summary>
    public string AutorDescricao { get; private set; }

    /// <summary>
    /// Frase deterministica, montada quando o evento aconteceu.
    ///
    /// Guardada por copia e nao remontada na exibicao, pelo mesmo motivo da
    /// explicacao de um sinal de risco: remontar faria uma mudanca futura de
    /// texto reescrever o passado.
    /// </summary>
    public string Descricao { get; private set; }

    /// <summary>
    /// O que o evento aponta: o alerta associado, o usuario que recebeu o
    /// caso, a nota adicionada. Nulo quando o evento nao aponta para nada.
    /// </summary>
    public Guid? ReferenciaId { get; private set; }

    public DateTimeOffset OcorridoEm { get; private set; }

    internal static EventoDoCaso Registrar(
        Guid organizacaoId,
        Guid casoId,
        int sequencia,
        TipoDeEventoDoCaso tipo,
        Guid autorId,
        string autorDescricao,
        string descricao,
        DateTimeOffset ocorridoEm,
        Guid? referenciaId = null)
    {
        if (organizacaoId == Guid.Empty || casoId == Guid.Empty || autorId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Evento de caso exige organizacao, caso e autor.");
        }

        if (!Enum.IsDefined(tipo))
        {
            throw new ViolacaoDeInvariante($"Tipo de evento de caso desconhecido: {tipo}.");
        }

        if (sequencia < 1)
        {
            throw new ViolacaoDeInvariante("A sequencia da timeline comeca em 1.");
        }

        return new EventoDoCaso(
            Identificador.Novo(),
            organizacaoId,
            casoId,
            sequencia,
            tipo,
            autorId,
            Encurtar(autorDescricao, TamanhoMaximoDoAutor),
            Encurtar(descricao, TamanhoMaximoDaDescricao),
            referenciaId,
            ocorridoEm);
    }

    private static string Encurtar(string? valor, int tamanhoMaximo)
    {
        var limpo = (valor ?? string.Empty).Trim();

        return limpo.Length <= tamanhoMaximo ? limpo : limpo[..tamanhoMaximo];
    }
}

/// <summary>
/// Uma nota de investigacao.
///
/// **Notas nao sao editaveis nem apagaveis, e isso e a politica** (ROADMAP
/// 7.7). Uma correcao relevante vira uma nota nova, que a timeline registra na
/// ordem em que aconteceu. Sobrescrever em silencio destruiria justamente o
/// que a nota deveria provar: o que o analista sabia, e quando.
///
/// Sobre HTML: o conteudo e **recusado**, e nunca limpo em silencio, quando
/// parece carregar marcacao. Limpar mudaria o que a pessoa escreveu sem avisar
/// — e uma nota de investigacao alterada pelo sistema e pior do que uma nota
/// recusada. A defesa de verdade contra XSS e a saida: a tela renderiza texto,
/// nunca HTML.
/// </summary>
public sealed class NotaDoCaso
{
    public const int TamanhoMinimoDoConteudo = 3;
    public const int TamanhoMaximoDoConteudo = 4_000;

    private NotaDoCaso(
        Guid id,
        Guid organizacaoId,
        Guid casoId,
        Guid autorId,
        string autorDescricao,
        string conteudo,
        DateTimeOffset criadaEm)
    {
        Id = id;
        OrganizacaoId = organizacaoId;
        CasoId = casoId;
        AutorId = autorId;
        AutorDescricao = autorDescricao;
        Conteudo = conteudo;
        CriadaEm = criadaEm;
    }

    // Construtor usado pelo EF Core na materializacao.
    private NotaDoCaso()
    {
        AutorDescricao = string.Empty;
        Conteudo = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizacaoId { get; private set; }

    public Guid CasoId { get; private set; }

    public Guid AutorId { get; private set; }

    public string AutorDescricao { get; private set; }

    /// <summary>Texto puro, exatamente como foi escrito.</summary>
    public string Conteudo { get; private set; }

    public DateTimeOffset CriadaEm { get; private set; }

    internal static NotaDoCaso Escrever(
        Guid organizacaoId,
        Guid casoId,
        Guid autorId,
        string autorDescricao,
        string conteudo,
        DateTimeOffset agora)
    {
        if (organizacaoId == Guid.Empty || casoId == Guid.Empty || autorId == Guid.Empty)
        {
            throw new ViolacaoDeInvariante("Nota exige organizacao, caso e autor.");
        }

        if (!TextoDeInvestigacao.EhValido(
                conteudo,
                TamanhoMinimoDoConteudo,
                TamanhoMaximoDoConteudo,
                "conteudo",
                out var texto,
                out var erro))
        {
            throw new ViolacaoDeInvariante(erro);
        }

        return new NotaDoCaso(
            Identificador.Novo(),
            organizacaoId,
            casoId,
            autorId,
            (autorDescricao ?? string.Empty).Trim(),
            texto,
            agora);
    }
}

/// <summary>
/// Validacao compartilhada dos textos que uma pessoa escreve na investigacao.
///
/// **Recusar, nunca limpar em silencio.** Um titulo ou uma nota que o sistema
/// altera sozinho deixa de ser o que a pessoa escreveu — e numa investigacao
/// isso e pior do que uma recusa com explicacao.
/// </summary>
public static class TextoDeInvestigacao
{
    /// <summary>
    /// Valida sem lancar, para que cada camada escolha como reagir.
    ///
    /// A regra vive aqui, em um lugar so. O dominio a usa e lanca invariante;
    /// a camada de aplicacao a usa e devolve erro de validacao, que e o codigo
    /// HTTP correto para "voce escreveu algo invalido" — 409 diria que o
    /// recurso esta num estado incompativel, o que seria mentira.
    /// </summary>
    public static bool EhValido(
        string? texto,
        int tamanhoMinimo,
        int tamanhoMaximo,
        string campo,
        out string limpo,
        out string erro)
    {
        limpo = (texto ?? string.Empty).Trim();

        if (limpo.Length < tamanhoMinimo || limpo.Length > tamanhoMaximo)
        {
            erro = $"O campo '{campo}' deve ter entre {tamanhoMinimo} e {tamanhoMaximo} caracteres.";
            return false;
        }

        if (PareceMarcacao(limpo))
        {
            erro = $"O campo '{campo}' nao pode conter marcacao. " +
                   "Escreva em texto puro — o sistema nao altera o que voce escreveu.";
            return false;
        }

        if (TemControleProibido(limpo))
        {
            erro = $"O campo '{campo}' contem caracteres de controle nao permitidos.";
            return false;
        }

        erro = string.Empty;
        return true;
    }

    /// <summary>
    /// Parece uma tag de marcacao?
    ///
    /// A regra e estreita de proposito: `&lt;` seguido de letra ou de barra.
    /// Assim `valor &lt; 100` continua sendo uma nota valida, enquanto
    /// `&lt;script&gt;` e `&lt;/b&gt;` sao recusados. Recusar todo `&lt;`
    /// tornaria impossivel escrever uma comparacao numerica — que e
    /// exatamente o tipo de coisa que se escreve investigando fraude.
    /// </summary>
    public static bool PareceMarcacao(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);

        for (var i = 0; i < texto.Length - 1; i++)
        {
            if (texto[i] == '<' && (char.IsLetter(texto[i + 1]) || texto[i + 1] == '/'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Caracteres de controle que nao tem lugar em texto escrito por gente.
    ///
    /// Quebra de linha e tabulacao passam: uma nota de investigacao tem
    /// paragrafos. O resto — bytes nulos, escapes de terminal — nao vem de um
    /// teclado e nao deve entrar no banco.
    /// </summary>
    public static bool TemControleProibido(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);

        foreach (var caractere in texto)
        {
            if (char.IsControl(caractere) && caractere is not ('\n' or '\r' or '\t'))
            {
                return true;
            }
        }

        return false;
    }
}
