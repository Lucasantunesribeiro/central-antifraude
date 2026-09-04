namespace CentralAntifraude.Application.Erros;

/// <summary>
/// Categorias de falha que a API distingue no contrato HTTP.
/// CLAUDE.md secao 103 exige que estas nao sejam confundidas entre si.
/// </summary>
public enum TipoDeErro
{
    /// <summary>Entrada malformada ou fora das regras de contrato. HTTP 400.</summary>
    Validacao,

    /// <summary>Credencial ausente ou invalida. HTTP 401.</summary>
    NaoAutenticado,

    /// <summary>Identidade valida sem permissao para a operacao. HTTP 403.</summary>
    NaoAutorizado,

    /// <summary>Recurso inexistente ou fora do tenant do solicitante. HTTP 404.</summary>
    NaoEncontrado,

    /// <summary>Estado atual incompativel com a operacao pedida. HTTP 409.</summary>
    Conflito,

    /// <summary>Excesso de requisicoes. HTTP 429.</summary>
    LimiteDeRequisicoes,

    /// <summary>Falha nao prevista. HTTP 500.</summary>
    Interno,
}

/// <summary>
/// Base de todas as falhas esperadas da aplicacao.
///
/// "Esperada" significa que o sistema sabe descrever a falha ao cliente sem
/// revelar nada de sua propria estrutura. Qualquer excecao que NAO herde
/// daqui e tratada como falha interna e nao tem sua mensagem exposta.
/// </summary>
public abstract class ErroDeAplicacao : Exception
{
    protected ErroDeAplicacao(string mensagem)
        : base(mensagem)
    {
    }

    protected ErroDeAplicacao(string mensagem, Exception causa)
        : base(mensagem, causa)
    {
    }

    /// <summary>Categoria usada para escolher o status HTTP.</summary>
    public abstract TipoDeErro Tipo { get; }

    /// <summary>
    /// Codigo estavel e legivel por maquina, para o cliente tratar o caso
    /// sem depender do texto da mensagem.
    /// </summary>
    public abstract string Codigo { get; }
}

/// <summary>Entrada recusada, com o detalhe por campo.</summary>
public sealed class ErroDeValidacao : ErroDeAplicacao
{
    public ErroDeValidacao(string mensagem, IReadOnlyDictionary<string, string[]> errosPorCampo)
        : base(mensagem)
    {
        ArgumentNullException.ThrowIfNull(errosPorCampo);
        ErrosPorCampo = errosPorCampo;
    }

    public ErroDeValidacao(string campo, string mensagem)
        : this("A requisicao contem campos invalidos.",
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [campo] = [mensagem] })
    {
    }

    public IReadOnlyDictionary<string, string[]> ErrosPorCampo { get; }

    public override TipoDeErro Tipo => TipoDeErro.Validacao;

    public override string Codigo => "validacao_falhou";
}

/// <summary>
/// Recurso inexistente OU pertencente a outro tenant.
///
/// Os dois casos sao deliberadamente indistinguiveis (CLAUDE.md secao 52):
/// responder 403 para um recurso de outro tenant ja confirmaria que aquele
/// identificador existe.
/// </summary>
public sealed class RecursoNaoEncontrado : ErroDeAplicacao
{
    public RecursoNaoEncontrado(string tipoDoRecurso)
        : base($"{tipoDoRecurso} nao encontrado.")
    {
        TipoDoRecurso = tipoDoRecurso;
    }

    public string TipoDoRecurso { get; }

    public override TipoDeErro Tipo => TipoDeErro.NaoEncontrado;

    public override string Codigo => "recurso_nao_encontrado";
}

/// <summary>
/// Operacao incompativel com o estado atual do recurso.
/// Ex.: mesma chave de idempotencia com conteudo diferente (Fase 2).
/// </summary>
public sealed class ConflitoDeEstado : ErroDeAplicacao
{
    public ConflitoDeEstado(string codigo, string mensagem)
        : base(mensagem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codigo);
        Codigo = codigo;
    }

    public override TipoDeErro Tipo => TipoDeErro.Conflito;

    public override string Codigo { get; }
}

/// <summary>
/// Credencial ausente, invalida ou sessao que nao vale mais.
///
/// A mensagem e sempre generica de proposito: distinguir "e-mail nao existe"
/// de "senha errada" entrega ao atacante metade do trabalho.
/// </summary>
public sealed class NaoAutenticado : ErroDeAplicacao
{
    public NaoAutenticado(string codigo, string mensagem)
        : base(mensagem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codigo);
        Codigo = codigo;
    }

    public override TipoDeErro Tipo => TipoDeErro.NaoAutenticado;

    public override string Codigo { get; }
}

/// <summary>
/// Identidade valida, mas sem permissao para a operacao.
///
/// Usado quando o RECURSO nao esta em jogo — por exemplo, um Auditor tentando
/// criar usuario. Quando o recurso pertence a outro tenant, a resposta correta
/// e <see cref="RecursoNaoEncontrado"/>, e nao esta: um 403 ali confirmaria a
/// existencia do identificador.
/// </summary>
public sealed class NaoAutorizado : ErroDeAplicacao
{
    public NaoAutorizado(string mensagem = "Seu perfil nao permite esta operacao.")
        : base(mensagem)
    {
    }

    public override TipoDeErro Tipo => TipoDeErro.NaoAutorizado;

    public override string Codigo => "acesso_negado";
}
