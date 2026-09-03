using CentralAntifraude.Api.Correlacao;
using CentralAntifraude.Domain.Primitivos;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CentralAntifraude.UnitTests.Api;

public sealed class MiddlewareDeCorrelacaoTests
{
    [Fact]
    public async Task Sem_cabecalho_gera_um_identificador_do_formato_adotado()
    {
        var (contexto, correlacao) = await Executar(cabecalhoRecebido: null);

        Assert.True(Guid.TryParse(correlacao.IdDeCorrelacao, out var identificador));
        Assert.True(Identificador.EhDoFormatoAdotado(identificador));
        Assert.Equal(
            correlacao.IdDeCorrelacao,
            contexto.Response.Headers[MiddlewareDeCorrelacao.NomeDoCabecalho].ToString());
    }

    [Fact]
    public async Task Aproveita_o_identificador_do_cliente_quando_ele_e_aceitavel()
    {
        // Preservar o valor do cliente e o que permite correlacionar um
        // incidente entre o sistema do integrador e a Central Antifraude.
        var (_, correlacao) = await Executar("pedido-abc-0001");

        Assert.Equal("pedido-abc-0001", correlacao.IdDeCorrelacao);
    }

    [Theory]
    [InlineData("curto")]                               // abaixo do minimo
    [InlineData("valor com espacos")]
    [InlineData("quebra\r\nInjected-Header: sim")]      // injecao de cabecalho
    [InlineData("<script>alert(1)</script>")]
    [InlineData("aspas\"e'apostrofos")]
    public async Task Recusa_valor_malformado_e_gera_um_proprio(string recebido)
    {
        // O valor volta num cabecalho de resposta e entra nos logs. Aceitar
        // texto livre permitiria forjar linha de log e injetar cabecalho.
        var (_, correlacao) = await Executar(recebido);

        Assert.NotEqual(recebido, correlacao.IdDeCorrelacao);
        Assert.True(Guid.TryParse(correlacao.IdDeCorrelacao, out _));
    }

    [Fact]
    public async Task Recusa_valor_longo_demais()
    {
        var enorme = new string('a', 5_000);

        var (_, correlacao) = await Executar(enorme);

        Assert.NotEqual(enorme, correlacao.IdDeCorrelacao);
        Assert.True(correlacao.IdDeCorrelacao.Length <= 64);
    }

    private static async Task<(HttpContext Contexto, ContextoDeCorrelacao Correlacao)> Executar(
        string? cabecalhoRecebido)
    {
        var contexto = new DefaultHttpContext();
        if (cabecalhoRecebido is not null)
        {
            contexto.Request.Headers[MiddlewareDeCorrelacao.NomeDoCabecalho] = cabecalhoRecebido;
        }

        var correlacao = new ContextoDeCorrelacao();

        var middleware = new MiddlewareDeCorrelacao(
            _ => Task.CompletedTask,
            NullLogger<MiddlewareDeCorrelacao>.Instance);

        await middleware.InvokeAsync(contexto, correlacao);

        return (contexto, correlacao);
    }
}
