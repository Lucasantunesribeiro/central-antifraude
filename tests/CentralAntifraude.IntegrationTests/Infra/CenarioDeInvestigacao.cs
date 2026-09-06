using System.Text.Json;
using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Risco;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests.Infra;

/// <summary>
/// Produz alertas de verdade para os testes de caso.
///
/// **Nada e inserido direto no banco.** O alerta nasce do caminho completo —
/// ingestao, avaliacao, Outbox, fila, worker — porque um caso construido sobre
/// um alerta forjado nao provaria que a investigacao funciona sobre o que o
/// sistema realmente produz.
/// </summary>
internal static class CenarioDeInvestigacao
{
    /// <summary>
    /// Ingere um cenario que produz `Revisar` (45) ou `Bloquear` (75) e devolve
    /// o alerta que o backbone criou.
    ///
    /// O historico e espacado em dias de proposito: em minutos, a regra de
    /// velocidade tambem acionaria e o score deixaria de ser o que o teste
    /// afirma.
    /// </summary>
    public static async Task<Alerta> GerarAlertaAsync(
        HttpClient http,
        string stringDeConexao,
        TenantDeTeste tenant,
        IntegracaoDeTeste integracao,
        CenarioDeMensageria mensageria,
        string prefixo,
        CancellationToken cancellationToken,
        bool bloquear = false)
    {
        ArgumentNullException.ThrowIfNull(mensageria);

        var clienteExterno = $"cli-{prefixo}-{Guid.NewGuid():N}"[..24];
        var inicio = DateTimeOffset.UtcNow.AddDays(-10);

        for (var i = 0; i < 3; i++)
        {
            using var historico = await CenarioDeIngestao.EnviarAsync(
                http,
                integracao.Chave,
                $"idem-{Guid.NewGuid():N}",
                CenarioDeIngestao.Corpo(
                    identificadorExterno: $"{prefixo}-hist-{i}",
                    // Valor baixo de proposito: e a media deste historico que a
                    // regra de valor compara. Com o padrao de 249,90 a
                    // transacao de 900 nao passaria de 5x a media, e o cenario
                    // de bloqueio silenciosamente viraria uma revisao.
                    valor: 100m,
                    clienteExternoId: clienteExterno,
                    ocorridaEm: inicio.AddDays(i),
                    fingerprintDoDispositivo: "disp-de-casa",
                    paisDeOrigem: "BR"),
                cancellationToken);

            historico.EnsureSuccessStatusCode();
        }

        using var alvo = await CenarioDeIngestao.EnviarAsync(
            http,
            integracao.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(
                identificadorExterno: $"{prefixo}-alvo",
                valor: bloquear ? 900m : 100m,
                clienteExternoId: clienteExterno,
                ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1),
                fingerprintDoDispositivo: "disp-novo",
                paisDeOrigem: "PT"),
            cancellationToken);

        alvo.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await alvo.Content.ReadAsStringAsync(cancellationToken));

        var esperada = bloquear ? Decisao.Bloquear : Decisao.Revisar;
        var decisao = json.RootElement.GetProperty("decisao").GetString();

        Assert.Equal(esperada.ToString(), decisao);

        var transacaoId = json.RootElement.GetProperty("id").GetGuid();

        await mensageria.RodarAteEsvaziarAsync(cancellationToken);

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            stringDeConexao,
            tenant.OrganizacaoId);

        return await contexto.Alertas.SingleAsync(a => a.TransacaoId == transacaoId, cancellationToken);
    }
}
