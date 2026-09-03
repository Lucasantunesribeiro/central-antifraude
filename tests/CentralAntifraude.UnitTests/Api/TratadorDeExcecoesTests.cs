using System.Text.Json;
using CentralAntifraude.Api.Erros;
using CentralAntifraude.Application.Erros;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace CentralAntifraude.UnitTests.Api;

/// <summary>
/// Security Gate 0: "stack traces nao expostos no contrato de producao".
///
/// Estes testes exercitam a classe real de producao contra um HttpContext de
/// verdade, sem subir servidor - entao provam o comportamento do contrato, e
/// nao um resumo dele.
/// </summary>
public sealed class TratadorDeExcecoesTests
{
    private const string SegredoInterno =
        "Npgsql: senha invalida para o usuario central_antifraude em 10.0.0.7";

    [Fact]
    public async Task Excecao_nao_prevista_vira_500_sem_revelar_nada_em_producao()
    {
        var (tratador, contexto, corpo) = Montar(ambiente: Environments.Production);

        var tratou = await tratador.TryHandleAsync(
            contexto,
            new InvalidOperationException(SegredoInterno),
            TestContext.Current.CancellationToken);

        Assert.True(tratou);
        Assert.Equal(StatusCodes.Status500InternalServerError, contexto.Response.StatusCode);

        var resposta = LerCorpo(corpo);

        Assert.DoesNotContain(SegredoInterno, resposta, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", resposta, StringComparison.Ordinal);
        Assert.DoesNotContain("at CentralAntifraude", resposta, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", resposta, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(resposta);
        Assert.Equal("falha_interna", json.RootElement.GetProperty("codigo").GetString());
        Assert.Equal(
            "Ocorreu uma falha inesperada ao processar a requisicao.",
            json.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Em_desenvolvimento_o_tipo_e_a_mensagem_ajudam_mas_o_stack_trace_continua_fora()
    {
        var (tratador, contexto, corpo) = Montar(ambiente: Environments.Development);

        await tratador.TryHandleAsync(
            contexto,
            LancarParaTerStackTraceReal(),
            TestContext.Current.CancellationToken);

        var resposta = LerCorpo(corpo);

        Assert.Contains("InvalidOperationException", resposta, StringComparison.Ordinal);
        // Ate em Development o stack trace fica fora da resposta: o lugar dele
        // e o log, que nao atravessa a rede.
        Assert.DoesNotContain("LancarParaTerStackTraceReal", resposta, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", resposta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Erro_de_validacao_vira_400_com_os_campos_recusados()
    {
        var (tratador, contexto, corpo) = Montar(ambiente: Environments.Production);

        var erro = new ErroDeValidacao(
            "A requisicao contem campos invalidos.",
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["valor"] = ["Deve ser maior que zero."],
            });

        await tratador.TryHandleAsync(contexto, erro, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status400BadRequest, contexto.Response.StatusCode);

        using var json = JsonDocument.Parse(LerCorpo(corpo));
        Assert.Equal("validacao_falhou", json.RootElement.GetProperty("codigo").GetString());
        Assert.Equal(
            "Deve ser maior que zero.",
            json.RootElement.GetProperty("erros").GetProperty("valor")[0].GetString());
    }

    [Fact]
    public async Task Recurso_de_outro_tenant_sai_como_404_e_nao_revela_que_existe()
    {
        var (tratador, contexto, corpo) = Montar(ambiente: Environments.Production);

        await tratador.TryHandleAsync(
            contexto,
            new RecursoNaoEncontrado("Transacao"),
            TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status404NotFound, contexto.Response.StatusCode);

        var resposta = LerCorpo(corpo);
        Assert.DoesNotContain("tenant", resposta, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permissao", resposta, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conflito_de_estado_vira_409_com_o_codigo_do_dominio()
    {
        var (tratador, contexto, corpo) = Montar(ambiente: Environments.Production);

        await tratador.TryHandleAsync(
            contexto,
            new ConflitoDeEstado("idempotencia_conflitante", "Chave ja usada com outro conteudo."),
            TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status409Conflict, contexto.Response.StatusCode);

        using var json = JsonDocument.Parse(LerCorpo(corpo));
        Assert.Equal("idempotencia_conflitante", json.RootElement.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task Cliente_que_desiste_da_requisicao_nao_vira_erro_de_servidor()
    {
        var (tratador, contexto, _) = Montar(ambiente: Environments.Production);

        using var cancelamento = new CancellationTokenSource();
        await cancelamento.CancelAsync();
        contexto.RequestAborted = cancelamento.Token;

        var tratou = await tratador.TryHandleAsync(
            contexto,
            new OperationCanceledException(),
            TestContext.Current.CancellationToken);

        Assert.True(tratou);
        Assert.Equal(499, contexto.Response.StatusCode);
    }

    [Theory]
    [InlineData(TipoDeErro.Validacao, StatusCodes.Status400BadRequest)]
    [InlineData(TipoDeErro.NaoAutenticado, StatusCodes.Status401Unauthorized)]
    [InlineData(TipoDeErro.NaoAutorizado, StatusCodes.Status403Forbidden)]
    [InlineData(TipoDeErro.NaoEncontrado, StatusCodes.Status404NotFound)]
    [InlineData(TipoDeErro.Conflito, StatusCodes.Status409Conflict)]
    [InlineData(TipoDeErro.LimiteDeRequisicoes, StatusCodes.Status429TooManyRequests)]
    [InlineData(TipoDeErro.Interno, StatusCodes.Status500InternalServerError)]
    public void Cada_categoria_de_erro_tem_um_status_http_proprio(TipoDeErro tipo, int status)
    {
        Assert.Equal(status, MapeamentoDeErroHttp.StatusPara(tipo));
    }

    [Fact]
    public void Toda_categoria_de_erro_esta_mapeada()
    {
        // Se alguem adicionar uma categoria e esquecer da tabela, o novo valor
        // cairia silenciosamente em 500. Este teste quebra antes disso.
        foreach (var tipo in Enum.GetValues<TipoDeErro>())
        {
            var status = MapeamentoDeErroHttp.StatusPara(tipo);
            var esperado500 = tipo == TipoDeErro.Interno;

            Assert.True(
                esperado500 == (status == StatusCodes.Status500InternalServerError),
                $"A categoria {tipo} nao tem status HTTP proprio na tabela de mapeamento.");
            Assert.NotEmpty(MapeamentoDeErroHttp.TituloPara(tipo));
        }
    }

    private static InvalidOperationException LancarParaTerStackTraceReal()
    {
        try
        {
            throw new InvalidOperationException(SegredoInterno);
        }
        catch (InvalidOperationException excecao)
        {
            return excecao;
        }
    }

    private static (TratadorDeExcecoes Tratador, HttpContext Contexto, MemoryStream Corpo) Montar(
        string ambiente)
    {
        var servicos = new ServiceCollection();
        servicos.AddLogging();
        servicos.AddProblemDetails();
        servicos.AddSingleton<IHostEnvironment>(new AmbienteDeTeste(ambiente));

        var provedor = servicos.BuildServiceProvider();

        var corpo = new MemoryStream();
        var contexto = new DefaultHttpContext
        {
            RequestServices = provedor,
            Response = { Body = corpo },
        };
        contexto.Request.Path = "/rota/qualquer";

        var tratador = new TratadorDeExcecoes(
            provedor.GetRequiredService<IProblemDetailsService>(),
            provedor.GetRequiredService<IHostEnvironment>(),
            provedor.GetRequiredService<ILogger<TratadorDeExcecoes>>());

        return (tratador, contexto, corpo);
    }

    private static string LerCorpo(MemoryStream corpo)
    {
        corpo.Position = 0;
        using var leitor = new StreamReader(corpo, leaveOpen: true);
        return leitor.ReadToEnd();
    }

    /// <summary>Ambiente minimo, so para escolher entre Production e Development.</summary>
    private sealed class AmbienteDeTeste : IHostEnvironment
    {
        public AmbienteDeTeste(string nome) => EnvironmentName = nome;

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "CentralAntifraude.Testes";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
