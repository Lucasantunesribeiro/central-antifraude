using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.Domain.Backtests;

/// <summary>
/// Transforma o snapshot candidato em uma versao de perfil que o motor sabe
/// executar — em memoria, e so.
///
/// **Este e o ponto que faz o ROADMAP 9.4 valer.** Nao existe
/// <c>BacktestRiskEngine</c>: existe o mesmo <see cref="MotorDeRisco"/>,
/// recebendo uma versao de perfil montada de outra fonte. A semantica das
/// regras nao e reescrita em lugar nenhum — se fosse, o backtest passaria a
/// medir um sistema que nao e o que roda em producao, e o resultado
/// enganaria exatamente quem confia nele para decidir.
///
/// Nada do que sai daqui e adicionado ao contexto de persistencia.
/// </summary>
public static class PerfilSimulado
{
    public static VersaoDePerfilDeRisco Montar(
        Guid organizacaoId,
        PerfilCandidato candidato,
        DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(candidato);

        candidato.Validar();

        var versoes = candidato.Regras
            .Select(regra => VersaoDeRegra.ParaSimulacao(
                organizacaoId,
                regra.RegraId,
                regra.Tipo,
                regra.Configuracao,
                regra.Pontos,
                agora))
            .ToList();

        return VersaoDePerfilDeRisco.ParaSimulacao(
            organizacaoId,
            candidato.LimiarDeRevisao,
            candidato.LimiarDeBloqueio,
            versoes,
            agora);
    }
}
