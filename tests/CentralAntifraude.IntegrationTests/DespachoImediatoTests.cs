using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// O aviso que a API dá ao despachante logo depois de confirmar a transação.
///
/// **Por que este aviso existe.** O laço de dois segundos das fases anteriores
/// mantinha o banco acordado vinte e quatro horas por dia. No Neon isso custa
/// mais do que o plano gratuito oferece — o banco suspende sozinho depois de
/// cinco minutos ocioso, e um polling constante impede exatamente isso. Trocar
/// o laço por "avisa quando tem trabalho, e varre a cada quinze minutos"
/// resolve o custo sem trocar a garantia de entrega.
///
/// **E é aqui que a coisa pode dar errado em silêncio.** Se o aviso saísse de
/// dentro da transação, o despachante leria a Outbox antes do commit: numa vez
/// não acharia nada, e noutra publicaria um evento cuja transação ainda pode
/// ser desfeita por falha de serialização. Nenhum dos dois defeitos aparece
/// como erro — aparecem como alerta que não chega, ou como alerta de uma
/// transação que nunca existiu.
///
/// Por isso o teste não confere que o aviso aconteceu: confere **de onde ele
/// enxerga o mundo**. O espião abre uma conexão própria com o PostgreSQL, e
/// uma conexão de fora só enxerga o que já foi confirmado.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class DespachoImediatoTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private TenantDeTeste _tenant = null!;

    public DespachoImediatoTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"desp-{Guid.NewGuid():N}"[..14],
            Cancelamento);

        await CenarioDeMensageria.LimparAsync(_banco.StringDeConexao, Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
    }

    public async ValueTask DisposeAsync()
    {
        await _fabrica.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// O evento já está confirmado no banco quando o despachante é acordado.
    ///
    /// A leitura do espião é feita numa conexão que não participa da transação
    /// da ingestão. Se o aviso saísse cedo demais, ela veria zero.
    /// </summary>
    [Fact]
    public async Task O_despachante_e_acordado_depois_do_commit()
    {
        var espiao = new EspiaoDoDespachante(_banco.StringDeConexao);

        using var fabrica = _fabrica.WithWebHostBuilder(construtor =>
            construtor.ConfigureTestServices(servicos =>
                servicos.AddSingleton<IDespachanteImediato>(espiao)));

        using var cliente = fabrica.CreateClient();

        var integracao = await CenarioDeIngestao.CriarIntegracaoAsync(cliente, _tenant, Cancelamento);

        var resposta = await CenarioDeIngestao.EnviarAsync(
            cliente,
            integracao.Chave,
            Guid.NewGuid().ToString(),
            CenarioDeIngestao.Corpo(identificadorExterno: $"desp-{Guid.NewGuid():N}"[..20]),
            Cancelamento);

        resposta.EnsureSuccessStatusCode();

        Assert.Equal(1, espiao.Avisos);
        Assert.True(
            espiao.EventosVisiveisNoPrimeiroAviso >= 1,
            "O despachante foi acordado antes do commit: uma conexao de fora nao " +
            $"enxergou o evento na Outbox (viu {espiao.EventosVisiveisNoPrimeiroAviso}).");
    }

    /// <summary>
    /// Solicitar um backtest também acorda o despachante.
    ///
    /// **Este teste nasceu de um defeito em produção.** O aviso ao despachante
    /// tinha sido ligado só no caminho de ingestão, e o evento
    /// `BacktestSolicitado.v1` ficava na Outbox com zero tentativas até a
    /// varredura de quinze minutos passar. Não havia erro em lugar nenhum: o
    /// backtest ficava "Pendente", e quem pediu concluía que travou.
    ///
    /// A lição é mais ampla que o caso: enquanto o despacho imediato for
    /// chamada explícita, **todo produtor de evento** precisa lembrar dela. Um
    /// produtor novo que esquecer passa a falhar aqui, e não no ambiente
    /// publicado.
    /// </summary>
    [Fact]
    public async Task Solicitar_backtest_tambem_acorda_o_despachante()
    {
        var espiao = new EspiaoDoDespachante(_banco.StringDeConexao);

        using var fabrica = _fabrica.WithWebHostBuilder(construtor =>
            construtor.ConfigureTestServices(servicos =>
                servicos.AddSingleton<IDespachanteImediato>(espiao)));

        using var cliente = fabrica.CreateClient();

        var token = await CenarioDeIngestao.TokenDeAsync(
            cliente, _tenant, PerfilDeUsuario.SupervisorDeFraude, Cancelamento);

        using var requisicao = CenarioDeIdentidade.Autenticada(
            HttpMethod.Post, "/api/backtests", token);

        requisicao.Content = JsonContent.Create(new
        {
            limiarDeRevisao = 30,
            limiarDeBloqueio = 60,
            inicio = DateTimeOffset.UtcNow.AddDays(-30),
            fim = DateTimeOffset.UtcNow,
        });

        var resposta = await cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.Accepted, resposta.StatusCode);
        Assert.Equal(1, espiao.Avisos);
    }

    /// <summary>
    /// Ninguém é acordado quando a transação não chega a existir.
    ///
    /// Um payload recusado na validação não gera evento nenhum, e acordar o
    /// despachante ali seria uma invocação de Lambda paga por requisição para
    /// descobrir que não havia nada a publicar — de graça para quem estivesse
    /// mandando lixo em volume.
    /// </summary>
    [Fact]
    public async Task Requisicao_recusada_nao_acorda_ninguem()
    {
        var espiao = new EspiaoDoDespachante(_banco.StringDeConexao);

        using var fabrica = _fabrica.WithWebHostBuilder(construtor =>
            construtor.ConfigureTestServices(servicos =>
                servicos.AddSingleton<IDespachanteImediato>(espiao)));

        using var cliente = fabrica.CreateClient();

        var integracao = await CenarioDeIngestao.CriarIntegracaoAsync(cliente, _tenant, Cancelamento);

        var resposta = await CenarioDeIngestao.EnviarAsync(
            cliente,
            integracao.Chave,
            Guid.NewGuid().ToString(),
            CenarioDeIngestao.Corpo(valor: -1m),
            Cancelamento);

        Assert.False(resposta.IsSuccessStatusCode);
        Assert.Equal(0, espiao.Avisos);
    }

    /// <summary>
    /// Conta os avisos e, no primeiro deles, olha a Outbox por uma conexão
    /// própria — de fora da transação da ingestão.
    /// </summary>
    private sealed class EspiaoDoDespachante : IDespachanteImediato
    {
        private readonly string _stringDeConexao;

        public EspiaoDoDespachante(string stringDeConexao) => _stringDeConexao = stringDeConexao;

        public int Avisos { get; private set; }

        public int EventosVisiveisNoPrimeiroAviso { get; private set; } = -1;

        public async Task AcordarAsync(CancellationToken cancellationToken)
        {
            Avisos++;

            if (EventosVisiveisNoPrimeiroAviso >= 0)
            {
                return;
            }

            // SQL cru, e nao o DbContext.
            //
            // A Outbox tem filtro global de tenant desde a Fase 4, e um
            // contexto anonimo enxergaria zero linhas mesmo com a transacao ja
            // confirmada — o teste falharia dizendo "antes do commit" por um
            // motivo que nao tem nada a ver com commit. A conexao crua mede o
            // que a asserçao afirma medir: o que esta gravado na tabela.
            await using var conexao = new NpgsqlConnection(_stringDeConexao);
            await conexao.OpenAsync(cancellationToken);

            await using var comando = new NpgsqlCommand(
                "SELECT count(*) FROM eventos_de_saida WHERE publicado_em IS NULL",
                conexao);

            EventosVisiveisNoPrimeiroAviso =
                Convert.ToInt32(await comando.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
    }
}
