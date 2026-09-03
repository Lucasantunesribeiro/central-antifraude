using CentralAntifraude.Application.Erros;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CentralAntifraude.Api.Erros;

/// <summary>
/// Traduz excecao em resposta HTTP no formato Problem Details (RFC 9457).
///
/// Regra de seguranca central (CLAUDE.md secao 103): so a mensagem de um
/// <see cref="ErroDeAplicacao"/> chega ao cliente. Qualquer outra excecao vira
/// 500 com texto generico - stack trace, tipo da excecao, nome de tabela e
/// detalhe de driver nunca atravessam a borda em producao.
/// </summary>
public sealed partial class TratadorDeExcecoes : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly IHostEnvironment _ambiente;
    private readonly ILogger<TratadorDeExcecoes> _log;

    public TratadorDeExcecoes(
        IProblemDetailsService problemDetails,
        IHostEnvironment ambiente,
        ILogger<TratadorDeExcecoes> log)
    {
        _problemDetails = problemDetails;
        _ambiente = ambiente;
        _log = log;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var caminho = httpContext.Request.Path.Value ?? "/";

        // Cliente desistiu da requisicao. Nao e falha do servidor e nao ha
        // ninguem para receber a resposta - registrar como erro so poluiria
        // a metrica de falhas.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            RegistrarCancelamentoDoCliente(_log, caminho);
            httpContext.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            return true;
        }

        var problema = Traduzir(exception);

        if (problema.Status >= StatusCodes.Status500InternalServerError)
        {
            RegistrarFalhaNaoTratada(_log, caminho, exception);
        }
        else
        {
            problema.Extensions.TryGetValue("codigo", out var codigo);
            RegistrarRequisicaoRecusada(
                _log,
                caminho,
                problema.Status ?? 0,
                codigo?.ToString() ?? "-");
        }

        httpContext.Response.StatusCode = problema.Status ?? StatusCodes.Status500InternalServerError;

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problema,
        });
    }

    private ProblemDetails Traduzir(Exception excecao)
    {
        var problema = new ProblemDetails();

        if (excecao is ErroDeAplicacao erro)
        {
            problema.Status = MapeamentoDeErroHttp.StatusPara(erro.Tipo);
            problema.Title = MapeamentoDeErroHttp.TituloPara(erro.Tipo);
            problema.Detail = erro.Message;
            problema.Extensions["codigo"] = erro.Codigo;

            if (erro is ErroDeValidacao validacao)
            {
                problema.Extensions["erros"] = validacao.ErrosPorCampo;
            }
        }
        else
        {
            problema.Status = StatusCodes.Status500InternalServerError;
            problema.Title = MapeamentoDeErroHttp.TituloPara(TipoDeErro.Interno);
            problema.Detail = "Ocorreu uma falha inesperada ao processar a requisicao.";
            problema.Extensions["codigo"] = "falha_interna";

            // Fora de Development nada da excecao e revelado. Mesmo em
            // Development o stack trace fica de fora: ele vai para o log,
            // que e o lugar dele.
            if (_ambiente.IsDevelopment())
            {
                problema.Extensions["excecaoDeDesenvolvimento"] =
                    $"{excecao.GetType().Name}: {excecao.Message}";
            }
        }

        // Instance e idDeCorrelacao sao preenchidos por CustomizeProblemDetails
        // (Program.cs), que vale para toda resposta de erro da API.
        return problema;
    }

    // Mensagens geradas por fonte: sem alocacao de array de parametros e sem
    // formatacao quando o nivel esta desligado. Este tratador roda em todo
    // caminho de erro da API, entao o custo importa.
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Requisicao cancelada pelo cliente em {Caminho}.")]
    private static partial void RegistrarCancelamentoDoCliente(ILogger logger, string caminho);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Falha nao tratada em {Caminho}.")]
    private static partial void RegistrarFalhaNaoTratada(ILogger logger, string caminho, Exception excecao);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Requisicao recusada em {Caminho} com status {Status} e codigo {Codigo}.")]
    private static partial void RegistrarRequisicaoRecusada(
        ILogger logger,
        string caminho,
        int status,
        string codigo);
}
