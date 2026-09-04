using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Application.Risco;

/// <summary>
/// Quanto passado o motor enxerga.
///
/// Os dois limites existem para que o custo de avaliar seja previsivel: sem
/// eles, um cliente com anos de historico faria a consulta crescer sem teto
/// no caminho critico da ingestao.
/// </summary>
public sealed class OpcoesDeAvaliacao
{
    public const string Secao = "Avaliacao";

    /// <summary>
    /// Janela de historico carregada.
    ///
    /// Precisa ser maior que a maior janela de qualquer regra de velocidade —
    /// senao a regra perguntaria por um passado que o contexto nao carregou, e
    /// silenciaria sem que ninguem percebesse. Ha uma validacao para isso.
    /// </summary>
    public int DiasDeHistorico { get; set; } = 90;

    /// <summary>
    /// Teto de transacoes anteriores carregadas, das mais recentes para as
    /// mais antigas.
    ///
    /// Um cliente com dezenas de milhares de transacoes nao pode transformar
    /// a avaliacao em uma consulta ilimitada.
    /// </summary>
    public int MaximoDeTransacoesNoHistorico { get; set; } = 200;

    public TimeSpan JanelaDeHistorico => TimeSpan.FromDays(DiasDeHistorico);

    public void Validar()
    {
        if (DiasDeHistorico is < 1 or > 3_650)
        {
            throw new InvalidOperationException($"{Secao}:DiasDeHistorico deve estar entre 1 e 3650.");
        }

        if (MaximoDeTransacoesNoHistorico is < 10 or > 10_000)
        {
            throw new InvalidOperationException(
                $"{Secao}:MaximoDeTransacoesNoHistorico deve estar entre 10 e 10000.");
        }

        // A janela de historico precisa cobrir a maior janela que uma regra
        // de velocidade pode declarar - 24 horas, pelo limite de
        // ConfiguracaoDeVelocidade. Se nao cobrisse, a regra perguntaria por
        // um passado que o contexto nao carregou e ficaria calada: sem erro,
        // sem aviso, e com score menor do que o perfil pede.
        if (JanelaDeHistorico < TimeSpan.FromMinutes(1_440))
        {
            throw new InvalidOperationException(
                $"{Secao}:DiasDeHistorico precisa cobrir ao menos 24 horas, " +
                "que e a maior janela que uma regra de velocidade pode usar.");
        }
    }
}

/// <summary>
/// Carrega o passado do cliente que as regras vao consultar.
///
/// Contrato na Application e implementacao na Infrastructure porque a Fase 9
/// (backtest) vai precisar montar o MESMO contexto a partir de outra fonte,
/// sem duplicar a semantica das regras.
/// </summary>
public interface IProvedorDeContextoDeRisco
{
    /// <summary>
    /// Historico do cliente da transacao, anterior a ela.
    ///
    /// A propria transacao NAO entra: ela e o que esta sendo avaliado, e
    /// inclui-la faria a regra de valor comparar a transacao com ela mesma.
    /// </summary>
    Task<ContextoDeRisco> CarregarAsync(Transacao transacao, CancellationToken cancellationToken);
}

/// <summary>Acesso a perfis e regras de risco.</summary>
public interface IRepositorioDeRisco
{
    /// <summary>
    /// Versao de perfil que vale agora para o tenant atual.
    ///
    /// Traz as versoes de regra junto: o motor precisa delas na mesma leitura,
    /// e busca-las depois seria um N+1 no caminho critico.
    /// </summary>
    Task<VersaoDePerfilDeRisco?> BuscarVersaoAtivaDoPerfilAsync(CancellationToken cancellationToken);

    /// <summary>Avaliacao de uma transacao, com os sinais.</summary>
    Task<AvaliacaoDeRisco?> BuscarAvaliacaoPorTransacaoAsync(
        Guid transacaoId,
        CancellationToken cancellationToken);

    /// <summary>Avaliacoes de varias transacoes, para a listagem.</summary>
    Task<IReadOnlyDictionary<Guid, AvaliacaoDeRisco>> BuscarAvaliacoesPorTransacoesAsync(
        IReadOnlyCollection<Guid> transacoesIds,
        CancellationToken cancellationToken);

    /// <summary>Regras da organizacao com a versao vigente, para consulta.</summary>
    Task<IReadOnlyList<(Regra Regra, VersaoDeRegra Versao)>> ListarRegrasVigentesAsync(
        CancellationToken cancellationToken);

    void Adicionar(CatalogoProvisionado catalogo);

    void AdicionarAvaliacao(AvaliacaoDeRisco avaliacao);
}
