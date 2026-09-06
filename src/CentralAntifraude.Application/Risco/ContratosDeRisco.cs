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

    /// <summary>
    /// Sinais de varias avaliacoes, agrupados por avaliacao.
    ///
    /// Existe para a fila de alertas: ela precisa mostrar o que mais pesou em
    /// cada linha, e buscar sinal por alerta seria N+1 na tela que o analista
    /// mais abre. Ja vem na ordem estavel do produto — maior peso primeiro,
    /// depois pelo tipo.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<SinalDeRisco>>> BuscarSinaisPorAvaliacoesAsync(
        IReadOnlyCollection<Guid> avaliacoesIds,
        CancellationToken cancellationToken);

    /// <summary>Regras da organizacao com a versao vigente, para consulta.</summary>
    Task<IReadOnlyList<(Regra Regra, VersaoDeRegra Versao)>> ListarRegrasVigentesAsync(
        CancellationToken cancellationToken);

    void Adicionar(CatalogoProvisionado catalogo);

    void AdicionarAvaliacao(AvaliacaoDeRisco avaliacao);

    // -----------------------------------------------------------------------
    // Administracao de regras (Fase 8)
    //
    // Separado da leitura operacional acima de proposito: o que a fila e o
    // detalhe da transacao precisam e a regra VIGENTE. O que o Supervisor
    // precisa e a regra INTEIRA — inclusive rascunho, versoes antigas e
    // regras desativadas, que nao aparecem em lugar nenhum da operacao.
    // -----------------------------------------------------------------------

    /// <summary>Todas as regras do tenant, ativas ou nao, com rascunho.</summary>
    Task<IReadOnlyList<Regra>> ListarTodasAsRegrasAsync(CancellationToken cancellationToken);

    /// <summary>Uma regra do tenant. Nulo quando nao existe daqui.</summary>
    Task<Regra?> BuscarRegraPorIdAsync(Guid regraId, CancellationToken cancellationToken);

    /// <summary>Existe outra regra com este nome no tenant?</summary>
    Task<bool> ExisteRegraComNomeAsync(
        string nome,
        Guid exceto,
        CancellationToken cancellationToken);

    /// <summary>Historico completo de versoes de uma regra, da mais recente para a mais antiga.</summary>
    Task<IReadOnlyList<VersaoDeRegra>> ListarVersoesDaRegraAsync(
        Guid regraId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ultima versao publicada de cada regra informada.
    ///
    /// Uma consulta para o conjunto inteiro: compor a proxima versao do perfil
    /// buscando versao por regra seria N+1 numa operacao que ja e rara mas que
    /// nao tem motivo para ser lenta.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, VersaoDeRegra>> BuscarUltimasVersoesAsync(
        IReadOnlyCollection<Guid> regrasIds,
        CancellationToken cancellationToken);

    /// <summary>Perfil de risco do tenant. Nulo se a organizacao nunca foi provisionada.</summary>
    Task<PerfilDeRisco?> BuscarPerfilAsync(CancellationToken cancellationToken);

    void AdicionarRegra(Regra regra);

    void AdicionarVersaoDeRegra(VersaoDeRegra versao);

    void AdicionarVersaoDePerfil(VersaoDePerfilDeRisco versao);
}
