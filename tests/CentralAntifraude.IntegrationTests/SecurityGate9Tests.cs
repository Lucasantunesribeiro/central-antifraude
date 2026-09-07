using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Domain.Backtests;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Security Gate 9 — backtests (ROADMAP 9.9).
///
/// A superficie nova desta fase tem tres riscos que as anteriores nao tinham.
///
/// **Custo.** Uma execucao le milhares de transacoes e roda o motor duas vezes
/// para cada uma. Sem limite de janela, de volume e de execucoes simultaneas,
/// um unico usuario autenticado derruba a fila de backtests com cliques.
///
/// **Uma segunda porta para o motor.** Se a configuracao candidata viesse do
/// corpo da requisicao, o backtest seria um caminho para executar regra que
/// nunca passou pelo catalogo fechado. Ela nao vem: o servidor a monta a
/// partir do rascunho ja gravado.
///
/// **Efeito operacional disfarcado de simulacao.** Um backtest que criasse
/// alerta ou avaliacao contaminaria a operacao com dados de ensaio, e ninguem
/// conseguiria distinguir depois.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class SecurityGate9Tests : IAsyncLifetime
{
    private const string Caminho = "/api/backtests";

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenantA = null!;
    private TenantDeTeste _tenantB = null!;
    private IntegracaoDeTeste _integracao = null!;
    private CenarioDeMensageria _mensageria = null!;

    private readonly Dictionary<(Guid Organizacao, PerfilDeUsuario Perfil), string> _tokens = [];

    public SecurityGate9Tests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        var sufixo = $"{Guid.NewGuid():N}"[..8];

        _tenantA = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"sg9a-{sufixo}",
            Cancelamento);

        _tenantB = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"sg9b-{sufixo}",
            Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();
        _integracao = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenantA, Cancelamento);

        await CenarioDeMensageria.LimparAsync(_banco.StringDeConexao, Cancelamento);

        _mensageria = CenarioDeMensageria.Criar(_banco.StringDeConexao);
    }

    public async ValueTask DisposeAsync()
    {
        if (_mensageria is not null)
        {
            await _mensageria.DisposeAsync();
        }

        _cliente?.Dispose();

        if (_fabrica is not null)
        {
            await _fabrica.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Perfil
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(PerfilDeUsuario.AnalistaDeFraude)]
    [InlineData(PerfilDeUsuario.Auditor)]
    public async Task Quem_nao_supervisiona_nao_alcanca_backtest_nenhum(PerfilDeUsuario perfil)
    {
        // Executar backtest e supervisao, e a leitura vai junto: um backtest
        // nao e uma decisao, e um ensaio sobre uma decisao que ainda nao foi
        // tomada. O material do Auditor sao as decisoes reais.
        using var listagem = await EnviarAsync(HttpMethod.Get, Caminho, perfil: perfil);
        Assert.Equal(HttpStatusCode.Forbidden, listagem.StatusCode);

        using var criacao = await EnviarAsync(
            HttpMethod.Post,
            Caminho,
            PedidoDeLimiares(),
            perfil);

        Assert.Equal(HttpStatusCode.Forbidden, criacao.StatusCode);
    }

    [Fact]
    public async Task Sem_token_nao_ha_backtest()
    {
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, new Uri(Caminho, UriKind.Relative));
        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task A_credencial_de_integracao_nao_serve_para_backtest()
    {
        // Uma API key nunca vira sessao humana (CLAUDE.md secao 50).
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, new Uri(Caminho, UriKind.Relative));
        requisicao.Headers.TryAddWithoutValidation("Authorization", $"ApiKey {_integracao.Chave}");

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Isolamento entre tenants
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Backtest_de_outro_tenant_responde_404_e_nao_403()
    {
        // 404 e nao 403: um 403 confirmaria que aquele identificador existe
        // em algum lugar (CLAUDE.md secao 52).
        var doA = Id(await SolicitarAsync(_tenantA));

        using var resposta = await EnviarAsync(
            HttpMethod.Get,
            $"{Caminho}/{doA}",
            organizacao: _tenantB);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task Cancelar_backtest_de_outro_tenant_responde_404_e_nao_altera_nada()
    {
        var doA = await SolicitarAsync(_tenantA);
        var id = Id(doA);

        using var resposta = await EnviarAsync(
            HttpMethod.Post,
            $"{Caminho}/{id}/cancelamento",
            new { versao = doA.GetProperty("execucao").GetProperty("versao").GetInt32() },
            organizacao: _tenantB);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);

        // Conferido no banco, e nao so pela resposta HTTP.
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        var execucao = await contexto.ExecucoesDeBacktest.AsNoTracking().SingleAsync(
            e => e.Id == id,
            Cancelamento);

        Assert.Equal(StatusDoBacktest.Pendente, execucao.Status);
    }

    [Fact]
    public async Task A_listagem_de_um_tenant_nao_enxerga_o_outro()
    {
        await SolicitarAsync(_tenantA);
        await SolicitarAsync(_tenantB);

        var doA = await LerAsync(await EnviarAsync(HttpMethod.Get, Caminho, organizacao: _tenantA));
        var doB = await LerAsync(await EnviarAsync(HttpMethod.Get, Caminho, organizacao: _tenantB));

        Assert.Equal(1, doA.GetProperty("totalDeItens").GetInt64());
        Assert.Equal(1, doB.GetProperty("totalDeItens").GetInt64());

        Assert.NotEqual(
            doA.GetProperty("itens")[0].GetProperty("id").GetGuid(),
            doB.GetProperty("itens")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Regra_de_outro_tenant_como_candidata_responde_404()
    {
        // O identificador da regra vem do cliente. Sem conferir o tenant, o
        // Supervisor de A montaria um candidato com a configuracao de B — e o
        // resultado descreveria a estrategia antifraude do outro cliente.
        var regraDeB = await RegraPorTipoAsync(_tenantB, TipoDeRegra.VelocidadePorCliente);

        using var resposta = await EnviarAsync(
            HttpMethod.Post,
            Caminho,
            Pedido(regraDeB.GetProperty("id").GetGuid()),
            organizacao: _tenantA);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Contrato de entrada
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Campo_desconhecido_no_corpo_e_recusado()
    {
        // JSON estrito (CLAUDE.md secao 54): quem escreve "inicioo" precisa
        // descobrir na primeira chamada, e nao depois de uma execucao que
        // varreu a janela errada.
        using var resposta = await EnviarAsync(HttpMethod.Post, Caminho, new
        {
            regraId = (Guid?)null,
            limiarDeRevisao = 40,
            limiarDeBloqueio = 70,
            inicio = DateTimeOffset.UtcNow.AddDays(-10),
            fim = DateTimeOffset.UtcNow,
            organizacaoId = Guid.CreateVersion7(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("resultado")]
    [InlineData("candidato")]
    [InlineData("versao")]
    public async Task Campos_internos_nao_podem_chegar_pelo_corpo(string campo)
    {
        // O que decide o resultado — status, candidato, resultado — e do
        // servidor. Aceitar qualquer um deles pelo corpo transformaria o
        // backtest numa porta lateral para executar configuracao arbitraria.
        var corpo = new Dictionary<string, object?>
        {
            ["regraId"] = null,
            ["limiarDeRevisao"] = 40,
            ["limiarDeBloqueio"] = 70,
            ["inicio"] = DateTimeOffset.UtcNow.AddDays(-10),
            ["fim"] = DateTimeOffset.UtcNow,
            [campo] = "qualquer coisa",
        };

        using var resposta = await EnviarAsync(HttpMethod.Post, Caminho, corpo);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Theory]
    [InlineData(0, 70)]
    [InlineData(70, 40)]
    [InlineData(40, 101)]
    [InlineData(-5, 70)]
    public async Task Limiares_impossiveis_sao_recusados(int revisao, int bloqueio)
    {
        // As mesmas invariantes de uma versao de perfil de verdade. Simular o
        // que nao poderia ser publicado descreveria um estado inalcancavel.
        using var resposta = await EnviarAsync(
            HttpMethod.Post,
            Caminho,
            Pedido(null, revisao, bloqueio));

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("estado_do_backtest", await CodigoAsync(resposta));
    }

    // -----------------------------------------------------------------------
    // Custo e abuso
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Intervalo_abusivo_e_recusado_antes_de_qualquer_leitura()
    {
        // A recusa acontece no `POST`, e nao no worker: uma janela de dez anos
        // aceita e enfileirada ja custaria a leitura inteira.
        using var resposta = await EnviarAsync(HttpMethod.Post, Caminho, new
        {
            regraId = (Guid?)null,
            limiarDeRevisao = (int?)null,
            limiarDeBloqueio = (int?)null,
            inicio = DateTimeOffset.UtcNow.AddYears(-10),
            fim = DateTimeOffset.UtcNow,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        Assert.Equal(0, await contexto.ExecucoesDeBacktest.CountAsync(Cancelamento));
    }

    [Fact]
    public async Task Spam_de_execucoes_esbarra_no_limite_por_organizacao()
    {
        var aceitas = 0;
        var recusadas = 0;

        for (var i = 0; i < 6; i++)
        {
            using var resposta = await EnviarAsync(
                HttpMethod.Post,
                Caminho,
                Pedido(null, 30 + i, 60 + i));

            if (resposta.StatusCode == HttpStatusCode.Accepted)
            {
                aceitas++;
            }
            else
            {
                Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
                Assert.Equal("limite_de_execucoes", await CodigoAsync(resposta));
                recusadas++;
            }
        }

        Assert.Equal(2, aceitas);
        Assert.Equal(4, recusadas);
    }

    [Fact]
    public async Task O_limite_de_execucoes_e_por_organizacao_e_nao_global()
    {
        // Um limite global faria um cliente movimentado bloquear todos os
        // outros — negacao de servico entre tenants pela porta da frente.
        for (var i = 0; i < 2; i++)
        {
            using var doA = await EnviarAsync(
                HttpMethod.Post,
                Caminho,
                Pedido(null, 30 + i, 60 + i),
                organizacao: _tenantA);

            Assert.Equal(HttpStatusCode.Accepted, doA.StatusCode);
        }

        using var doB = await EnviarAsync(
            HttpMethod.Post,
            Caminho,
            PedidoDeLimiares(),
            organizacao: _tenantB);

        Assert.Equal(HttpStatusCode.Accepted, doB.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Mensageria
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Mensagem_que_declara_outro_tenant_nao_executa_nada()
    {
        // O envelope e dado, nunca autoridade. Uma mensagem forjada que
        // aponte para a execucao de A declarando o tenant B rodaria o backtest
        // de um cliente e o registraria como se fosse de outro.
        var execucao = Id(await SolicitarAsync(_tenantA));

        await _mensageria.Fila.EnviarAsync(
            IFilaDeMensagens.FilaDeBacktests,
            EnvelopeForjado(execucao, _tenantB.OrganizacaoId),
            Cancelamento);

        var resultado = await _mensageria.Backtests.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(0, resultado.Processados);
        Assert.Equal(1, resultado.Recusados);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        Assert.Equal(
            StatusDoBacktest.Pendente,
            (await contexto.ExecucoesDeBacktest.AsNoTracking()
                .SingleAsync(e => e.Id == execucao, Cancelamento)).Status);
    }

    [Fact]
    public async Task Mensagem_apontando_para_execucao_inexistente_vai_para_o_redrive()
    {
        await _mensageria.Fila.EnviarAsync(
            IFilaDeMensagens.FilaDeBacktests,
            EnvelopeForjado(Guid.CreateVersion7(), _tenantA.OrganizacaoId),
            Cancelamento);

        // Nao se apaga o que nao se entende: a mensagem circula ate a politica
        // de redrive leva-la para a fila de mortas.
        for (var i = 0; i < _mensageria.Opcoes.MaximoDeRecebimentos + 1; i++)
        {
            await _mensageria.Backtests.ConsumirLoteAsync(Cancelamento);
        }

        Assert.Equal(
            1,
            await _mensageria.Fila.ContarMortasAsync(
                IFilaDeMensagens.FilaDeBacktests,
                Cancelamento));
    }

    [Fact]
    public async Task Corpo_ilegivel_na_fila_de_backtests_nao_derruba_o_worker()
    {
        await _mensageria.Fila.EnviarAsync(
            IFilaDeMensagens.FilaDeBacktests,
            "{ isto nao e json",
            Cancelamento);

        var resultado = await _mensageria.Backtests.ConsumirLoteAsync(Cancelamento);

        Assert.Equal(1, resultado.Recusados);
        Assert.Equal(0, resultado.Processados);
    }

    [Fact]
    public async Task Dois_workers_na_mesma_execucao_produzem_uma_conclusao_so()
    {
        // Worker duplicado (ROADMAP 9.9). O token de versao arbitra: quem
        // perde a corrida sai sem escrever. Duas conclusoes reescreveriam um
        // resultado que alguem ja pode ter lido.
        var execucao = Id(await SolicitarAsync(_tenantA));

        await _mensageria.Despachante.DespacharLoteAsync(Cancelamento);

        // Uma copia a mais da MESMA mensagem: e assim que dois workers pegam a
        // mesma execucao ao mesmo tempo, apesar da visibilidade da fila.
        await _mensageria.Fila.EnviarAsync(
            IFilaDeMensagens.FilaDeBacktests,
            await CorpoDaFilaAsync(),
            Cancelamento);

        await using var outro = CenarioDeMensageria.Criar(_banco.StringDeConexao);

        var resultados = await Task.WhenAll(
            Task.Run(() => _mensageria.Backtests.ConsumirLoteAsync(Cancelamento), Cancelamento),
            Task.Run(() => outro.Backtests.ConsumirLoteAsync(Cancelamento), Cancelamento));

        // Exatamente uma conclusao. O outro worker sai por "nada a fazer" —
        // ou porque perdeu o token, ou porque encontrou a execucao encerrada.
        Assert.Equal(1, resultados.Sum(r => r.Processados));

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        var linha = await contexto.ExecucoesDeBacktest.AsNoTracking()
            .SingleAsync(e => e.Id == execucao, Cancelamento);

        Assert.Equal(StatusDoBacktest.Concluida, linha.Status);
        Assert.NotNull(linha.Resultado);
    }

    // -----------------------------------------------------------------------
    // Efeito operacional
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Um_backtest_que_bloquearia_tudo_nao_cria_alerta_nenhum()
    {
        // O risco mais sutil da fase: um candidato severo que produzisse
        // alertas contaminaria a fila do analista com dados de ensaio, e
        // ninguem conseguiria distinguir depois qual alerta era real.
        await IngerirAsync("gate9", quantidade: 4);
        await _mensageria.RodarAteEsvaziarAsync(Cancelamento);

        var alertasAntes = await ContarAlertasAsync();
        var avaliacoesAntes = await ContarAvaliacoesAsync();

        var regra = await RegraPorTipoAsync(_tenantA, TipoDeRegra.VelocidadePorCliente);
        var regraId = regra.GetProperty("id").GetGuid();

        await SalvarRascunhoSeveroAsync(regraId, regra.GetProperty("versao").GetInt32());

        var execucao = Id(await SolicitarAsync(_tenantA, regraId));

        await _mensageria.RodarBacktestsAteEsvaziarAsync(Cancelamento);

        // O ciclo operacional tambem roda: se o backtest tivesse deixado
        // qualquer evento na Outbox, e aqui que ele viraria alerta.
        await _mensageria.RodarAteEsvaziarAsync(Cancelamento);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        var linha = await contexto.ExecucoesDeBacktest.AsNoTracking()
            .SingleAsync(e => e.Id == execucao, Cancelamento);

        Assert.Equal(StatusDoBacktest.Concluida, linha.Status);
        Assert.True(linha.Resultado!.Candidato.Bloquear > 0, "o candidato precisa bloquear algo");

        Assert.Equal(alertasAntes, await ContarAlertasAsync());
        Assert.Equal(avaliacoesAntes, await ContarAvaliacoesAsync());
    }

    // =======================================================================
    // Apoio
    // =======================================================================

    private static Guid Id(JsonElement detalhe) =>
        detalhe.GetProperty("execucao").GetProperty("id").GetGuid();

    private static object Pedido(Guid? regraId, int? revisao = null, int? bloqueio = null) => new
    {
        regraId,
        limiarDeRevisao = revisao,
        limiarDeBloqueio = bloqueio,
        inicio = DateTimeOffset.UtcNow.AddDays(-10),
        fim = DateTimeOffset.UtcNow.AddMinutes(5),
    };

    private static object PedidoDeLimiares() => Pedido(null, 35, 65);

    private async Task<JsonElement> SolicitarAsync(TenantDeTeste tenant, Guid? regraId = null)
    {
        using var resposta = await EnviarAsync(
            HttpMethod.Post,
            Caminho,
            Pedido(regraId),
            organizacao: tenant);

        Assert.Equal(HttpStatusCode.Accepted, resposta.StatusCode);

        return await LerAsync(resposta);
    }

    /// <summary>
    /// Um envelope montado a mao, como faria quem conseguisse escrever na
    /// fila. O formato de fio e o mesmo que a Fase 14 vai mandar para a SQS.
    /// </summary>
    private static string EnvelopeForjado(Guid execucaoId, Guid tenantId) =>
        JsonSerializer.Serialize(new
        {
            eventId = Guid.CreateVersion7().ToString(),
            eventType = "BacktestSolicitado",
            version = 1,
            tenantId = tenantId.ToString(),
            correlationId = "forjado-000001",
            occurredAt = DateTimeOffset.UtcNow.ToString("O"),
            payload = new { execucaoId = execucaoId.ToString() },
        });

    private async Task<string> CorpoDaFilaAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        return await contexto.FilaDeMensagens
            .AsNoTracking()
            .Where(m => m.Fila == IFilaDeMensagens.FilaDeBacktests)
            .Select(m => m.Corpo)
            .FirstAsync(Cancelamento);
    }

    private async Task<JsonElement> RegraPorTipoAsync(TenantDeTeste tenant, TipoDeRegra tipo)
    {
        var regras = await LerAsync(await EnviarAsync(
            HttpMethod.Get,
            "/api/regras/gestao",
            organizacao: tenant));

        return regras.EnumerateArray().First(r => r.GetProperty("tipo").GetString() == tipo.ToString());
    }

    private async Task SalvarRascunhoSeveroAsync(Guid regraId, int versao)
    {
        using var resposta = await EnviarAsync(
            HttpMethod.Put,
            $"/api/regras/{regraId}/rascunho",
            new
            {
                nome = "Velocidade por cliente",
                configuracao = new Dictionary<string, decimal>
                {
                    ["maximoDeTransacoes"] = 1,
                    ["janelaEmMinutos"] = 1_440,
                },
                pontos = 100,
                versao,
            });

        resposta.EnsureSuccessStatusCode();
    }

    private async Task IngerirAsync(string prefixo, int quantidade)
    {
        var cliente = $"cli-{prefixo}-{Guid.NewGuid():N}"[..24];
        var inicio = DateTimeOffset.UtcNow.AddMinutes(-quantidade * 3);

        for (var i = 0; i < quantidade; i++)
        {
            using var resposta = await CenarioDeIngestao.EnviarAsync(
                _cliente,
                _integracao.Chave,
                $"idem-{Guid.NewGuid():N}",
                CenarioDeIngestao.Corpo(
                    identificadorExterno: $"{prefixo}-{i}-{Guid.NewGuid():N}"[..24],
                    valor: 100m,
                    clienteExternoId: cliente,
                    ocorridaEm: inicio.AddMinutes(i * 3),
                    fingerprintDoDispositivo: "disp-de-casa",
                    paisDeOrigem: "BR"),
                Cancelamento);

            resposta.EnsureSuccessStatusCode();
        }
    }

    private async Task<int> ContarAlertasAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        return await contexto.Alertas.CountAsync(Cancelamento);
    }

    private async Task<int> ContarAvaliacoesAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        return await contexto.AvaliacoesDeRisco.CountAsync(Cancelamento);
    }

    private async Task<HttpResponseMessage> EnviarAsync(
        HttpMethod metodo,
        string caminho,
        object? corpo = null,
        PerfilDeUsuario perfil = PerfilDeUsuario.SupervisorDeFraude,
        TenantDeTeste? organizacao = null)
    {
        var tenant = organizacao ?? _tenantA;
        var token = await TokenAsync(tenant, perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(metodo, caminho, token);

        if (corpo is not null)
        {
            requisicao.Content = JsonContent.Create(corpo);
        }

        return await _cliente.SendAsync(requisicao, Cancelamento);
    }

    private async Task<string> TokenAsync(TenantDeTeste tenant, PerfilDeUsuario perfil)
    {
        var chave = (tenant.OrganizacaoId, perfil);

        if (_tokens.TryGetValue(chave, out var existente))
        {
            return existente;
        }

        var token = await CenarioDeIngestao.TokenDeAsync(_cliente, tenant, perfil, Cancelamento);
        _tokens[chave] = token;

        return token;
    }

    private static async Task<JsonElement> LerAsync(HttpResponseMessage resposta)
    {
        var corpo = await resposta.Content.ReadAsStringAsync(Cancelamento);

        return JsonDocument.Parse(corpo).RootElement.Clone();
    }

    private static async Task<string?> CodigoAsync(HttpResponseMessage resposta)
    {
        var problema = await LerAsync(resposta);

        return problema.TryGetProperty("codigo", out var codigo) ? codigo.GetString() : null;
    }
}
