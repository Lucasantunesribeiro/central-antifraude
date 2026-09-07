using System.Reflection;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.ArchitectureTests;

/// <summary>
/// Vigia a exigencia do ROADMAP 9.4: **nao existe um segundo motor**.
///
/// O backtest so significa alguma coisa se rodar exatamente o codigo que a
/// producao roda. Se alguem escrever um <c>BacktestRiskEngine</c> — por um
/// motivo legitimo, como "o backtest precisa de uma otimizacao aqui" — o
/// resultado passa a descrever um sistema que nao e o que decide de verdade,
/// e quem confia nele para publicar uma regra e enganado sem saber.
///
/// A verificacao nao prova pureza: ela garante que a duplicacao precisa ser
/// deliberada, e nao acidental.
/// </summary>
public sealed class MotorUnicoTests
{
    private static readonly Assembly Dominio = typeof(MotorDeRisco).Assembly;

    [Fact]
    public void So_existe_um_tipo_que_avalia_uma_transacao_contra_um_perfil()
    {
        // A assinatura e a definicao de "motor": recebe transacao e versao de
        // perfil, devolve avaliacao. Qualquer outro tipo com essa forma seria
        // um segundo motor, com nome diferente.
        var motores = Dominio
            .GetTypes()
            .Where(tipo => tipo
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Any(EhAvaliacaoCompleta))
            .Select(tipo => tipo.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal([nameof(MotorDeRisco)], motores);
    }

    [Fact]
    public void Cada_tipo_de_regra_tem_exatamente_um_avaliador()
    {
        // Dois avaliadores para o mesmo tipo seriam duas semanticas para a
        // mesma regra — e o backtest poderia acabar executando a outra.
        var avaliadores = Dominio
            .GetTypes()
            .Where(tipo => typeof(IAvaliadorDeRegra).IsAssignableFrom(tipo))
            .Where(tipo => tipo is { IsInterface: false, IsAbstract: false })
            .Select(tipo => (IAvaliadorDeRegra)Activator.CreateInstance(tipo)!)
            .ToList();

        var duplicados = avaliadores
            .GroupBy(avaliador => avaliador.Tipo)
            .Where(grupo => grupo.Count() > 1)
            .Select(grupo => grupo.Key.ToString())
            .ToList();

        Assert.True(
            duplicados.Count == 0,
            $"Tipos com mais de um avaliador: {string.Join(", ", duplicados)}.");
    }

    [Fact]
    public void O_motor_padrao_cobre_o_catalogo_inteiro()
    {
        // Um tipo do catalogo sem avaliador registrado faz o motor recusar o
        // perfil que o contiver. Melhor descobrir aqui do que numa avaliacao.
        var suportados = new MotorDeRisco().TiposSuportados;

        Assert.Equal(
            Enum.GetValues<TipoDeRegra>().Order().ToArray(),
            suportados.Order().ToArray());
    }

    private static bool EhAvaliacaoCompleta(MethodInfo metodo)
    {
        if (metodo.ReturnType != typeof(AvaliacaoDeRisco))
        {
            return false;
        }

        var parametros = metodo.GetParameters().Select(p => p.ParameterType).ToList();

        return parametros.Contains(typeof(Transacao))
            && parametros.Contains(typeof(VersaoDePerfilDeRisco));
    }
}
