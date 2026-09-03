using CentralAntifraude.Infrastructure.Tempo;

namespace CentralAntifraude.UnitTests.Infraestrutura;

public sealed class RelogioSistemaTests
{
    [Fact]
    public void Agora_sempre_vem_em_utc()
    {
        // CLAUDE.md secao 100: instantes sao persistidos em UTC e o horario
        // local do servidor nao e autoridade. Se esta implementacao passar a
        // devolver horario local, toda regra de janela temporal das fases
        // seguintes fica errada por um numero inteiro de horas - o tipo de
        // defeito que so aparece em producao, num fuso diferente do da maquina
        // de desenvolvimento.
        var relogio = new RelogioSistema();

        Assert.Equal(TimeSpan.Zero, relogio.Agora.Offset);
    }

    [Fact]
    public void Agora_avanca()
    {
        var relogio = new RelogioSistema();

        var primeiro = relogio.Agora;
        var segundo = relogio.Agora;

        Assert.True(segundo >= primeiro);
    }
}
