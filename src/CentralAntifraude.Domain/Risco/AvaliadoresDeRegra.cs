using System.Globalization;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Domain.Risco;

/// <summary>
/// O que uma regra produz quando dispara.
///
/// <see cref="Explicacao"/> e texto deterministico montado aqui — nunca por
/// IA (CLAUDE.md secao 19 e ROADMAP 3.6). A mesma entrada produz sempre a
/// mesma frase, e por isso ela pode ser gravada junto do sinal e continuar
/// verdadeira anos depois.
///
/// <see cref="DadosDaEvidencia"/> guarda os numeros que sustentam a frase, em
/// pares nome/valor, para que uma tela possa apresenta-los sem reinterpretar
/// texto.
/// </summary>
public sealed record SinalCalculado(
    TipoDeRegra Tipo,
    string Explicacao,
    IReadOnlyDictionary<string, string> DadosDaEvidencia);

/// <summary>
/// Avalia um tipo de regra. Funcao pura: mesma entrada, mesmo resultado.
///
/// Nao ha acesso a banco, a relogio nem a rede. Tudo o que a regra pode saber
/// chega em <see cref="ContextoDeRisco"/> — e e isso que torna o motor
/// testavel sem infraestrutura e reutilizavel pelo backtest da Fase 9.
/// </summary>
public interface IAvaliadorDeRegra
{
    TipoDeRegra Tipo { get; }

    /// <summary>Devolve o sinal quando a regra dispara, ou nulo quando nao ha nada a dizer.</summary>
    SinalCalculado? Avaliar(
        ConfiguracaoDeRegra configuracao,
        Transacao transacao,
        ContextoDeRisco contexto);
}

/// <summary>
/// Muitas tentativas do mesmo cliente em uma janela curta.
///
/// O padrao que isso reconhece e o card testing: varias tentativas em
/// sequencia para descobrir quais credenciais ainda funcionam.
/// </summary>
public sealed class AvaliadorDeVelocidade : IAvaliadorDeRegra
{
    public TipoDeRegra Tipo => TipoDeRegra.VelocidadePorCliente;

    public SinalCalculado? Avaliar(
        ConfiguracaoDeRegra configuracao,
        Transacao transacao,
        ContextoDeRisco contexto)
    {
        ArgumentNullException.ThrowIfNull(transacao);
        ArgumentNullException.ThrowIfNull(contexto);

        if (configuracao is not ConfiguracaoDeVelocidade config)
        {
            return null;
        }

        var janela = TimeSpan.FromMinutes(config.JanelaEmMinutos);

        // A janela e ancorada em OcorridaEm, e nao no relogio do servidor:
        // uma rajada que chega atrasada continua sendo uma rajada.
        var anteriores = contexto.QuantidadeNaJanela(transacao.OcorridaEm, janela);

        // +1 para contar a transacao que esta sendo avaliada. Sem isso, o
        // limite significaria "N anteriores mais esta", e o numero
        // configurado nao seria o que o operador acha que configurou.
        var totalNaJanela = anteriores + 1;

        if (totalNaJanela <= config.MaximoDeTransacoes)
        {
            return null;
        }

        return new SinalCalculado(
            Tipo,
            $"{totalNaJanela} tentativas deste cliente em {config.JanelaEmMinutos} minuto(s), " +
            $"acima do limite de {config.MaximoDeTransacoes}.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tentativasNaJanela"] = totalNaJanela.ToString(CultureInfo.InvariantCulture),
                ["limiteConfigurado"] = config.MaximoDeTransacoes.ToString(CultureInfo.InvariantCulture),
                ["janelaEmMinutos"] = config.JanelaEmMinutos.ToString(CultureInfo.InvariantCulture),
            });
    }
}

/// <summary>
/// Dispositivo nunca visto antes para aquele cliente.
///
/// Exige historico minimo de proposito: para quem nunca comprou, TODO
/// dispositivo e novo, e o sinal nao separaria nada.
/// </summary>
public sealed class AvaliadorDeNovoDispositivo : IAvaliadorDeRegra
{
    public TipoDeRegra Tipo => TipoDeRegra.NovoDispositivo;

    public SinalCalculado? Avaliar(
        ConfiguracaoDeRegra configuracao,
        Transacao transacao,
        ContextoDeRisco contexto)
    {
        ArgumentNullException.ThrowIfNull(transacao);
        ArgumentNullException.ThrowIfNull(contexto);

        if (configuracao is not ConfiguracaoDeNovoDispositivo config)
        {
            return null;
        }

        // Sem fingerprint nao ha o que comparar. Tratar ausencia como
        // "dispositivo novo" transformaria falta de dado em sinal de risco.
        if (string.IsNullOrWhiteSpace(transacao.FingerprintDoDispositivo))
        {
            return null;
        }

        if (contexto.TotalDeTransacoes < config.MinimoDeTransacoesNoHistorico)
        {
            return null;
        }

        if (contexto.DispositivoConhecido(transacao.FingerprintDoDispositivo))
        {
            return null;
        }

        return new SinalCalculado(
            Tipo,
            $"Dispositivo nao visto nas {contexto.TotalDeTransacoes} transacao(oes) anteriores deste cliente.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["transacoesNoHistorico"] = contexto.TotalDeTransacoes.ToString(CultureInfo.InvariantCulture),
            });
    }
}

/// <summary>
/// Valor muito acima do que aquele cliente costuma gastar.
///
/// Compara com a MEDIA na mesma moeda. O maximo historico seria pior: uma
/// unica compra atipica no passado calaria a regra para sempre.
/// </summary>
public sealed class AvaliadorDeValorAcimaDoHistorico : IAvaliadorDeRegra
{
    public TipoDeRegra Tipo => TipoDeRegra.ValorAcimaDoHistorico;

    public SinalCalculado? Avaliar(
        ConfiguracaoDeRegra configuracao,
        Transacao transacao,
        ContextoDeRisco contexto)
    {
        ArgumentNullException.ThrowIfNull(transacao);
        ArgumentNullException.ThrowIfNull(contexto);

        if (configuracao is not ConfiguracaoDeValorAcimaDoHistorico config)
        {
            return null;
        }

        var moeda = transacao.Valor.Moeda;

        // Comparar reais com dolares produziria um numero sem significado.
        if (contexto.TotalNaMoeda(moeda) < config.MinimoDeTransacoesNoHistorico)
        {
            return null;
        }

        if (contexto.ValorMedio(moeda) is not { } media || media <= 0)
        {
            return null;
        }

        var limite = decimal.Round(media * config.MultiploDaMedia, 4);

        if (transacao.Valor.Valor <= limite)
        {
            return null;
        }

        var quantasVezes = decimal.Round(transacao.Valor.Valor / media, 2);

        return new SinalCalculado(
            Tipo,
            $"Valor {quantasVezes.ToString("0.##", CultureInfo.InvariantCulture)}x a media historica " +
            $"deste cliente em {moeda} ({media.ToString("0.00", CultureInfo.InvariantCulture)}).",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["valorDaTransacao"] = transacao.Valor.Valor.ToString("0.0000", CultureInfo.InvariantCulture),
                ["mediaHistorica"] = media.ToString("0.0000", CultureInfo.InvariantCulture),
                ["limiteConfigurado"] = limite.ToString("0.0000", CultureInfo.InvariantCulture),
                ["moeda"] = moeda,
            });
    }
}

/// <summary>
/// Pais de origem diferente dos ja vistos para aquele cliente.
/// </summary>
public sealed class AvaliadorDeDivergenciaGeografica : IAvaliadorDeRegra
{
    public TipoDeRegra Tipo => TipoDeRegra.DivergenciaGeografica;

    public SinalCalculado? Avaliar(
        ConfiguracaoDeRegra configuracao,
        Transacao transacao,
        ContextoDeRisco contexto)
    {
        ArgumentNullException.ThrowIfNull(transacao);
        ArgumentNullException.ThrowIfNull(contexto);

        if (configuracao is not ConfiguracaoDeDivergenciaGeografica config)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(transacao.PaisDeOrigem))
        {
            return null;
        }

        var conhecidos = contexto.PaisesConhecidos;

        // Exige historico COM pais: se as anteriores nao trouxeram o campo,
        // nao existe padrao geografico do qual divergir.
        if (conhecidos.Count < 1 || contexto.TotalDeTransacoes < config.MinimoDeTransacoesNoHistorico)
        {
            return null;
        }

        if (contexto.PaisConhecido(transacao.PaisDeOrigem))
        {
            return null;
        }

        return new SinalCalculado(
            Tipo,
            $"Origem em {transacao.PaisDeOrigem}, diferente do historico deste cliente " +
            $"({string.Join(", ", conhecidos)}).",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["paisDaTransacao"] = transacao.PaisDeOrigem,
                ["paisesConhecidos"] = string.Join(",", conhecidos),
            });
    }
}
