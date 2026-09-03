using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.Api.Correlacao;

/// <summary>
/// Da a cada requisicao um identificador de correlacao e o devolve na resposta.
///
/// O valor enviado pelo cliente e aproveitado apenas se passar na validacao de
/// formato. Isso importa por dois motivos concretos:
/// 1. o valor volta em um cabecalho de resposta e entra nos logs estruturados -
///    aceitar texto livre permitiria forjar linhas de log e injetar cabecalho;
/// 2. sem teto de tamanho, um cliente poderia inflar todo log da operacao.
/// </summary>
public sealed class MiddlewareDeCorrelacao
{
    public const string NomeDoCabecalho = "X-Correlation-Id";

    private const int TamanhoMinimo = 8;
    private const int TamanhoMaximo = 64;

    private readonly RequestDelegate _proximo;
    private readonly ILogger<MiddlewareDeCorrelacao> _log;

    public MiddlewareDeCorrelacao(RequestDelegate proximo, ILogger<MiddlewareDeCorrelacao> log)
    {
        _proximo = proximo;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext contexto, ContextoDeCorrelacao correlacao)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentNullException.ThrowIfNull(correlacao);

        var recebido = contexto.Request.Headers[NomeDoCabecalho].ToString();
        var idDeCorrelacao = EhAceitavel(recebido)
            ? recebido
            : Identificador.Novo().ToString();

        correlacao.Definir(idDeCorrelacao);
        contexto.Response.Headers[NomeDoCabecalho] = idDeCorrelacao;

        // Escopo de log: toda linha emitida durante a requisicao carrega o
        // identificador, que e o que permite seguir o fluxo ponta a ponta.
        using (_log.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["CorrelationId"] = idDeCorrelacao,
        }))
        {
            await _proximo(contexto);
        }
    }

    private static bool EhAceitavel(string? valor)
    {
        if (valor is null || valor.Length < TamanhoMinimo || valor.Length > TamanhoMaximo)
        {
            return false;
        }

        foreach (var caractere in valor)
        {
            var permitido =
                caractere is >= 'a' and <= 'z' ||
                caractere is >= 'A' and <= 'Z' ||
                caractere is >= '0' and <= '9' ||
                caractere is '-' or '_';

            if (!permitido)
            {
                return false;
            }
        }

        return true;
    }
}
