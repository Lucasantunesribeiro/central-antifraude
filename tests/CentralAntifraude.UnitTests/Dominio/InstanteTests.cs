using CentralAntifraude.Domain.Tempo;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// O truncamento que impede um horario de mudar ao passar pelo banco.
///
/// Este teste existe por causa de um defeito real encontrado na Fase 3: a
/// resposta da ingestao devolvia <c>avaliadaEm</c> da memoria na primeira
/// requisicao e do banco no retry, e os dois diferiam em nanossegundos porque
/// o <c>timestamptz</c> do PostgreSQL guarda microssegundos. Bug real vira
/// protecao permanente (CLAUDE.md secao 89).
/// </summary>
public class InstanteTests
{
    [Fact]
    public void Normalizar_descarta_o_que_o_banco_nao_guardaria()
    {
        // 7943419 ticks de fracao: o banco guardaria ate 794341 microssegundos
        // e devolveria .7943410.
        var comTicksExtras = new DateTimeOffset(2026, 9, 4, 12, 20, 7, TimeSpan.Zero)
            .AddTicks(7_943_419);

        var normalizado = Instante.Normalizar(comTicksExtras);

        Assert.Equal(0, normalizado.Ticks % Instante.TicksPorMicrossegundo);
        Assert.Equal(comTicksExtras.AddTicks(-9), normalizado);
    }

    [Fact]
    public void Normalizar_trunca_e_nao_arredonda()
    {
        // Arredondar produziria a divergencia ao contrario: o valor em memoria
        // ficaria acima do que o banco guardou.
        var acimaDaMetade = DateTimeOffset.UnixEpoch.AddTicks(9);

        Assert.Equal(DateTimeOffset.UnixEpoch, Instante.Normalizar(acimaDaMetade));
    }

    [Fact]
    public void Normalizar_converte_para_utc()
    {
        var comOffset = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.FromHours(-3));

        var normalizado = Instante.Normalizar(comOffset);

        Assert.Equal(TimeSpan.Zero, normalizado.Offset);
        Assert.Equal(12, normalizado.Hour);
    }

    [Fact]
    public void Normalizar_e_idempotente()
    {
        var uma = Instante.Normalizar(DateTimeOffset.UnixEpoch.AddTicks(1_234_567));

        Assert.Equal(uma, Instante.Normalizar(uma));
    }
}
