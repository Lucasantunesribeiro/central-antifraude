using System.Globalization;

namespace CentralAntifraude.Domain.Risco;

/// <summary>Natureza de um campo de configuracao.</summary>
public enum TipoDoCampo
{
    Inteiro = 1,
    Fracionario = 2,
}

/// <summary>
/// Um campo configuravel de um tipo de regra.
///
/// E a partir desta descricao que o frontend monta o formulario (ROADMAP
/// 8.3). Os limites vem daqui e sao os MESMOS que a configuracao valida — um
/// teste confere campo a campo, porque duas listas de limites que saem de
/// sincronia produziriam uma tela que aceita o que o dominio recusa.
/// </summary>
public sealed record CampoDeConfiguracao(
    string Nome,
    string Rotulo,
    TipoDoCampo Tipo,
    decimal Minimo,
    decimal Maximo,
    decimal Padrao);

/// <summary>Um tipo do catalogo fechado, descrito para quem vai configura-lo.</summary>
public sealed record DescricaoDeTipoDeRegra(
    TipoDeRegra Tipo,
    string Rotulo,
    string Resumo,
    int PontosSugeridos,
    IReadOnlyList<CampoDeConfiguracao> Campos);

/// <summary>
/// O catalogo fechado de tipos de regra, descrito e montavel.
///
/// **Isto e o que impede o produto de virar uma DSL** (CLAUDE.md secao 21).
/// A administracao de regras nao recebe expressao, SQL nem script: recebe um
/// tipo do catalogo e um punhado de numeros nomeados. O <c>switch</c> de
/// <see cref="TentarMontar"/> e a lista fechada do que pode existir — um tipo
/// novo exige codigo novo aqui, e nao uma linha nova no banco.
///
/// Tres recusas explicitas, e todas viram <c>400</c>:
///
/// - campo desconhecido, porque aceitar em silencio faria quem enviou
///   acreditar que o valor foi usado;
/// - campo faltando, porque um padrao escolhido pelo servidor mudaria o
///   comportamento do motor sem ninguem ter decidido;
/// - valor fora da faixa, com a faixa na mensagem.
/// </summary>
public static class CatalogoDeTiposDeRegra
{
    /// <summary>
    /// Peso minimo de uma regra administrada.
    ///
    /// Zero seria uma regra que dispara e nao soma nada: apareceria na
    /// explicacao como evidencia sem peso, e ninguem saberia dizer se foi
    /// engano ou intencao.
    /// </summary>
    public const int PontosMinimos = 1;

    private const string MinimoDeHistorico = "minimoDeTransacoesNoHistorico";

    private static readonly CampoDeConfiguracao CampoDeMinimoDeHistorico = new(
        MinimoDeHistorico,
        "Minimo de transacoes no historico",
        TipoDoCampo.Inteiro,
        Minimo: 1,
        Maximo: 100,
        Padrao: 3);

    /// <summary>Todos os tipos que este motor sabe executar, na ordem do catalogo.</summary>
    public static IReadOnlyList<DescricaoDeTipoDeRegra> Todos { get; } =
    [
        new(
            TipoDeRegra.VelocidadePorCliente,
            "Velocidade por cliente",
            "Muitas tentativas do mesmo cliente em uma janela curta.",
            PontosSugeridos: 35,
            [
                new(
                    "maximoDeTransacoes",
                    "Maximo de transacoes na janela",
                    TipoDoCampo.Inteiro,
                    Minimo: 1,
                    Maximo: 1_000,
                    Padrao: 3),
                new(
                    "janelaEmMinutos",
                    "Janela em minutos",
                    TipoDoCampo.Inteiro,
                    Minimo: 1,
                    Maximo: 1_440,
                    Padrao: 10),
            ]),

        new(
            TipoDeRegra.NovoDispositivo,
            "Dispositivo novo",
            "Dispositivo nunca visto antes para aquele cliente.",
            PontosSugeridos: 20,
            [CampoDeMinimoDeHistorico]),

        new(
            TipoDeRegra.ValorAcimaDoHistorico,
            "Valor acima do historico",
            "Valor muito acima do que aquele cliente costuma gastar.",
            PontosSugeridos: 30,
            [
                new(
                    "multiploDaMedia",
                    "Multiplo da media do cliente",
                    TipoDoCampo.Fracionario,
                    Minimo: 1.1m,
                    Maximo: 100m,
                    Padrao: 5m),
                CampoDeMinimoDeHistorico,
            ]),

        new(
            TipoDeRegra.DivergenciaGeografica,
            "Divergencia geografica",
            "Pais de origem diferente do que aquele cliente costuma usar.",
            PontosSugeridos: 25,
            [CampoDeMinimoDeHistorico]),
    ];

    public static DescricaoDeTipoDeRegra Descrever(TipoDeRegra tipo) =>
        Todos.FirstOrDefault(d => d.Tipo == tipo)
        ?? throw new ViolacaoDeInvariante($"Tipo de regra fora do catalogo: {tipo}.");

    /// <summary>
    /// Monta a configuracao tipada a partir dos numeros enviados.
    ///
    /// Devolve <c>false</c> com uma mensagem legivel em vez de lancar: quem
    /// chama transforma isso em erro de validacao, e o Supervisor precisa
    /// saber qual campo esta errado — nao apenas que "a configuracao e
    /// invalida".
    /// </summary>
    public static bool TentarMontar(
        TipoDeRegra tipo,
        IReadOnlyDictionary<string, decimal>? valores,
        out ConfiguracaoDeRegra? configuracao,
        out string erro)
    {
        configuracao = null;

        if (!Enum.IsDefined(tipo))
        {
            erro = "Tipo de regra fora do catalogo.";
            return false;
        }

        var descricao = Descrever(tipo);
        var enviados = valores ?? new Dictionary<string, decimal>(StringComparer.Ordinal);

        // Campo desconhecido e recusa, e nao silencio: aceitar faria quem
        // enviou acreditar que aquele numero foi usado pelo motor.
        var desconhecido = enviados.Keys
            .FirstOrDefault(nome => !descricao.Campos.Any(c => string.Equals(c.Nome, nome, StringComparison.Ordinal)));

        if (desconhecido is not null)
        {
            erro = $"Campo '{desconhecido}' nao existe na configuracao de {descricao.Rotulo}.";
            return false;
        }

        var lidos = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var campo in descricao.Campos)
        {
            if (!enviados.TryGetValue(campo.Nome, out var valor))
            {
                erro = $"Campo '{campo.Nome}' e obrigatorio para {descricao.Rotulo}.";
                return false;
            }

            if (campo.Tipo == TipoDoCampo.Inteiro && decimal.Truncate(valor) != valor)
            {
                erro = $"Campo '{campo.Nome}' aceita apenas numero inteiro.";
                return false;
            }

            if (valor < campo.Minimo || valor > campo.Maximo)
            {
                erro = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Campo '{campo.Nome}' deve estar entre {campo.Minimo:0.##} e {campo.Maximo:0.##}.");

                return false;
            }

            lidos[campo.Nome] = valor;
        }

        // Lista fechada. Um tipo novo exige codigo novo aqui - que e
        // exatamente a barreira que impede configuracao vinda do cliente de
        // virar comportamento arbitrario.
        configuracao = tipo switch
        {
            TipoDeRegra.VelocidadePorCliente => new ConfiguracaoDeVelocidade(
                (int)lidos["maximoDeTransacoes"],
                (int)lidos["janelaEmMinutos"]),

            TipoDeRegra.NovoDispositivo => new ConfiguracaoDeNovoDispositivo(
                (int)lidos[MinimoDeHistorico]),

            TipoDeRegra.ValorAcimaDoHistorico => new ConfiguracaoDeValorAcimaDoHistorico(
                lidos["multiploDaMedia"],
                (int)lidos[MinimoDeHistorico]),

            TipoDeRegra.DivergenciaGeografica => new ConfiguracaoDeDivergenciaGeografica(
                (int)lidos[MinimoDeHistorico]),

            _ => null,
        };

        if (configuracao is null)
        {
            erro = $"Nao ha contrato de configuracao para o tipo {tipo}.";
            return false;
        }

        // Segunda camada: a propria configuracao valida de novo. As faixas
        // acima existem para dar mensagem por campo; a autoridade continua
        // sendo o dominio.
        try
        {
            configuracao.Validar();
        }
        catch (ViolacaoDeInvariante excecao)
        {
            configuracao = null;
            erro = excecao.Message;

            return false;
        }

        erro = string.Empty;

        return true;
    }

    /// <summary>
    /// Volta da configuracao tipada para os numeros nomeados.
    ///
    /// E o que permite abrir um rascunho existente no formulario com os
    /// valores preenchidos, sem que a tela precise saber a forma interna de
    /// cada record.
    /// </summary>
    public static IReadOnlyDictionary<string, decimal> Desmontar(ConfiguracaoDeRegra configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        return configuracao switch
        {
            ConfiguracaoDeVelocidade c => new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                ["maximoDeTransacoes"] = c.MaximoDeTransacoes,
                ["janelaEmMinutos"] = c.JanelaEmMinutos,
            },

            ConfiguracaoDeNovoDispositivo c => new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                [MinimoDeHistorico] = c.MinimoDeTransacoesNoHistorico,
            },

            ConfiguracaoDeValorAcimaDoHistorico c => new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                ["multiploDaMedia"] = c.MultiploDaMedia,
                [MinimoDeHistorico] = c.MinimoDeTransacoesNoHistorico,
            },

            ConfiguracaoDeDivergenciaGeografica c => new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                [MinimoDeHistorico] = c.MinimoDeTransacoesNoHistorico,
            },

            _ => throw new ViolacaoDeInvariante(
                $"Nao ha descricao de campos para {configuracao.GetType().Name}."),
        };
    }
}
