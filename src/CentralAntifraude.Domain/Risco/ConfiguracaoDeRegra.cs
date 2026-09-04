namespace CentralAntifraude.Domain.Risco;

/// <summary>
/// Configuracao de uma regra: os numeros que ela usa, e nada mais.
///
/// Cada tipo do catalogo tem exatamente um contrato de configuracao, com
/// campos tipados e validacao propria. Nao ha expressao, condicao nem
/// consulta configuravel — configurar e escolher valores dentro de um
/// contrato conhecido.
///
/// A validacao roda ANTES de a versao ser publicada. Uma configuracao
/// invalida nao chega ao banco, e portanto nunca chega ao motor (ROADMAP
/// secao 3.7).
/// </summary>
public abstract record ConfiguracaoDeRegra
{
    public abstract TipoDeRegra Tipo { get; }

    /// <exception cref="ViolacaoDeInvariante">Se a configuracao for invalida.</exception>
    public abstract void Validar();

    /// <summary>
    /// Resumo legivel da configuracao, usado na explicacao do sinal e nas
    /// telas. Deterministico: a mesma configuracao produz sempre o mesmo
    /// texto.
    /// </summary>
    public abstract string Descrever();

    protected static void Exigir(bool condicao, string mensagem)
    {
        if (!condicao)
        {
            throw new ViolacaoDeInvariante(mensagem);
        }
    }
}

/// <summary>
/// Quantas tentativas do mesmo cliente cabem em uma janela de tempo.
///
/// **Pratica real.** U.S. Payments Forum, "Card-Not-Present (CNP) Fraud
/// Mitigation Techniques" (2020), secao 8: "Velocity checks monitor the
/// number of times that certain transaction data elements occur within
/// certain intervals and look for anomalies or similarities to known fraud
/// behavior", e "A velocity check is made up of three or more variables,
/// always including quantity, data element, and timeframe" — que sao
/// exatamente <see cref="MaximoDeTransacoes"/>, o cliente e
/// <see cref="JanelaEmMinutos"/>.
///
/// **Configuracao de demonstracao.** Os valores do seed nao sao recomendacao
/// de mercado. O proprio documento so traz um exemplo ilustrativo — "five
/// transactions in 15 minutes" — e nao um numero prescrito.
/// </summary>
public sealed record ConfiguracaoDeVelocidade(int MaximoDeTransacoes, int JanelaEmMinutos)
    : ConfiguracaoDeRegra
{
    public override TipoDeRegra Tipo => TipoDeRegra.VelocidadePorCliente;

    public override void Validar()
    {
        Exigir(MaximoDeTransacoes is >= 1 and <= 1_000, "MaximoDeTransacoes deve estar entre 1 e 1000.");
        Exigir(JanelaEmMinutos is >= 1 and <= 1_440, "JanelaEmMinutos deve estar entre 1 e 1440.");
    }

    public override string Descrever() =>
        $"mais de {MaximoDeTransacoes} tentativa(s) em {JanelaEmMinutos} minuto(s)";
}

/// <summary>
/// Dispositivo nunca visto antes para aquele cliente.
///
/// **Pratica real.** U.S. Payments Forum (2020), secao 10: "Browser
/// identifiers are a subset of more general device identifiers that provide a
/// means to identify [a device]" e "If a device is unknown, the enterprise
/// could use additional step-up authentication".
///
/// <see cref="MinimoDeTransacoesNoHistorico"/> existe para nao punir o
/// primeiro acesso: sem historico, TODO dispositivo e novo, e o sinal nao
/// diria nada. A regra so fala quando ha um padrao do qual divergir.
/// </summary>
public sealed record ConfiguracaoDeNovoDispositivo(int MinimoDeTransacoesNoHistorico)
    : ConfiguracaoDeRegra
{
    public override TipoDeRegra Tipo => TipoDeRegra.NovoDispositivo;

    public override void Validar() =>
        Exigir(
            MinimoDeTransacoesNoHistorico is >= 1 and <= 100,
            "MinimoDeTransacoesNoHistorico deve estar entre 1 e 100.");

    public override string Descrever() =>
        $"dispositivo nao visto antes, com ao menos {MinimoDeTransacoesNoHistorico} transacao(oes) no historico";
}

/// <summary>
/// Valor muito acima do que aquele cliente costuma gastar.
///
/// **Pratica real.** U.S. Payments Forum (2020), secao 13, lista entre os
/// gatilhos de alerta ao portador: "Transaction amount &gt; baseline spending".
///
/// A base de comparacao e a MEDIA do historico, e nao o maior valor ja visto:
/// usar o maximo faria uma unica compra atipica no passado silenciar a regra
/// para sempre.
/// </summary>
public sealed record ConfiguracaoDeValorAcimaDoHistorico(
    decimal MultiploDaMedia,
    int MinimoDeTransacoesNoHistorico) : ConfiguracaoDeRegra
{
    public override TipoDeRegra Tipo => TipoDeRegra.ValorAcimaDoHistorico;

    public override void Validar()
    {
        Exigir(
            MultiploDaMedia is >= 1.1m and <= 100m,
            "MultiploDaMedia deve estar entre 1,1 e 100.");
        Exigir(
            MinimoDeTransacoesNoHistorico is >= 1 and <= 100,
            "MinimoDeTransacoesNoHistorico deve estar entre 1 e 100.");
    }

    public override string Descrever() =>
        $"valor acima de {MultiploDaMedia:0.##}x a media do cliente, " +
        $"com ao menos {MinimoDeTransacoesNoHistorico} transacao(oes) no historico";
}

/// <summary>
/// Pais de origem diferente do que aquele cliente costuma usar.
///
/// **Pratica real.** U.S. Payments Forum (2020), secao 13, lista entre os
/// gatilhos de alerta: "Geolocation — New place, first transaction with large
/// amount".
///
/// O pais vem declarado pela integracao, e nao de uma base GeoIP. A Central
/// Antifraude nao guarda o endereco IP (ADR 0007), so o HMAC dele — entao
/// nao ha como derivar localizacao aqui, e inventar uma seria pior do que
/// confiar no dado que a origem ja tem.
/// </summary>
public sealed record ConfiguracaoDeDivergenciaGeografica(int MinimoDeTransacoesNoHistorico)
    : ConfiguracaoDeRegra
{
    public override TipoDeRegra Tipo => TipoDeRegra.DivergenciaGeografica;

    public override void Validar() =>
        Exigir(
            MinimoDeTransacoesNoHistorico is >= 1 and <= 100,
            "MinimoDeTransacoesNoHistorico deve estar entre 1 e 100.");

    public override string Descrever() =>
        $"pais diferente dos ja vistos, com ao menos {MinimoDeTransacoesNoHistorico} transacao(oes) no historico";
}
