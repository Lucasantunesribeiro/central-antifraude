using CentralAntifraude.Application.Erros;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Tempo;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Application.Risco;

/// <summary>
/// Avalia o risco de uma transacao recem-registrada.
///
/// A sequencia e a do ROADMAP secao 3.5, e a ordem importa:
///
/// <code>
/// carregar contexto  ->  montar RiskContext  ->  executar regras
/// </code>
///
/// O contexto e carregado UMA vez, antes de qualquer regra rodar. Se cada
/// regra consultasse por conta propria, duas delas poderiam enxergar
/// historicos diferentes dentro da mesma avaliacao — e o resultado deixaria
/// de ser explicavel.
/// </summary>
public sealed class ServicoDeAvaliacaoDeRisco
{
    private readonly IRepositorioDeRisco _risco;
    private readonly IProvedorDeContextoDeRisco _contexto;
    private readonly MotorDeRisco _motor;
    private readonly IRelogio _relogio;

    public ServicoDeAvaliacaoDeRisco(
        IRepositorioDeRisco risco,
        IProvedorDeContextoDeRisco contexto,
        MotorDeRisco motor,
        IRelogio relogio)
    {
        _risco = risco;
        _contexto = contexto;
        _motor = motor;
        _relogio = relogio;
    }

    /// <summary>
    /// Avalia e registra o resultado. Nao grava — quem chama decide quando
    /// confirmar, para que transacao e avaliacao entrem na mesma gravacao.
    /// </summary>
    public async Task<AvaliacaoDeRisco> AvaliarAsync(
        Transacao transacao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transacao);

        var versaoDoPerfil = await _risco.BuscarVersaoAtivaDoPerfilAsync(cancellationToken)
            ?? throw new PerfilDeRiscoAusente();

        var contexto = await _contexto.CarregarAsync(transacao, cancellationToken);

        var avaliacao = _motor.Avaliar(transacao, versaoDoPerfil, contexto, _relogio.Agora);

        _risco.AdicionarAvaliacao(avaliacao);

        return avaliacao;
    }
}

/// <summary>
/// A organizacao nao tem perfil de risco publicado.
///
/// Nao e caso de negocio: e defeito de provisionamento. O catalogo padrao e
/// criado junto da organizacao, entao isto so acontece se alguem tiver
/// inserido uma organizacao por fora.
///
/// **Falha fechada.** Registrar a transacao sem avaliar devolveria ao
/// integrador uma resposta sem decisao — e o silencio seria lido como
/// "passou". Melhor recusar e deixar claro que ha configuracao faltando.
/// </summary>
public sealed class PerfilDeRiscoAusente : ErroDeAplicacao
{
    public PerfilDeRiscoAusente()
        : base("A organizacao nao possui perfil de risco publicado. Nao e possivel avaliar a transacao.")
    {
    }

    public override TipoDeErro Tipo => TipoDeErro.Interno;

    public override string Codigo => "perfil_de_risco_ausente";
}
