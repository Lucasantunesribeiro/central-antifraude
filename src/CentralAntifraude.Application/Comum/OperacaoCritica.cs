namespace CentralAntifraude.Application.Comum;

/// <summary>
/// Executa uma operacao dentro do boundary transacional forte do sistema.
///
/// **Onde isto se aplica, e onde nao.** Somente na ingestao de transacao. Ler
/// uma lista, autenticar, criar usuario ou rotacionar credencial nao dependem
/// de simultaneidade para estarem corretos, e ligar isolamento forte neles so
/// traria retry onde nao ha conflito (CLAUDE.md secao 35 e ROADMAP 4.3).
///
/// **Por que a ingestao precisa.** A avaliacao de risco le o passado do
/// cliente e decide a partir dele. Duas transacoes simultaneas do mesmo
/// cliente, cada uma sem enxergar a outra, produziriam duas avaliacoes que
/// nenhuma execucao sequencial produziria — a quarta tentativa de uma rajada
/// nao veria a terceira e a regra de velocidade ficaria calada nas duas.
///
/// **O que se promete.** O resultado equivale a ALGUMA ordem serial valida
/// das operacoes concorrentes. Nao se promete que toda requisicao enxergue as
/// simultaneas: isso seria exigir que ela enxergasse o futuro.
/// </summary>
public interface IExecutorDeOperacaoCritica
{
    /// <summary>
    /// Roda <paramref name="operacao"/> em uma transacao serializavel,
    /// repetindo a operacao INTEIRA em caso de conflito de concorrencia
    /// reconhecido.
    ///
    /// A operacao precisa ser reexecutavel: ela sera chamada de novo, do
    /// zero, com estado relido. Nao guarde nada fora da transacao entre
    /// tentativas.
    /// </summary>
    Task<T> ExecutarAsync<T>(
        Func<CancellationToken, Task<T>> operacao,
        CancellationToken cancellationToken);
}

/// <summary>Ajustes do retry de concorrencia.</summary>
public sealed class OpcoesDeConcorrencia
{
    public const string Secao = "Concorrencia";

    /// <summary>
    /// Quantas vezes a operacao inteira pode rodar, contando a primeira.
    ///
    /// Pequeno de proposito (ROADMAP 4.4). Retry de concorrencia resolve
    /// disputa curta; se quatro execucoes seguidas conflitam, o problema nao e
    /// azar — e contencao real, que insistir so piora.
    /// </summary>
    public int MaximoDeTentativas { get; set; } = 8;

    /// <summary>Espera base entre tentativas, em milissegundos.</summary>
    public int EsperaBaseEmMs { get; set; } = 10;

    /// <summary>Teto da espera entre tentativas, em milissegundos.</summary>
    public int EsperaMaximaEmMs { get; set; } = 200;

    /// <summary>
    /// Custo por linha que a transacao critica informa ao planejador.
    ///
    /// **Isto nao e microotimizacao. E o que impede uma tempestade de
    /// conflitos falsos.** A documentacao do PostgreSQL (Transaction
    /// Isolation, Serializable Isolation Level, consultada em 2026-09-04) e
    /// literal:
    ///
    /// > "A sequential scan will always necessitate a relation-level predicate
    /// > lock. This can result in an increased rate of serialization failures.
    /// > It may be helpful to encourage the use of index scans by reducing
    /// > random_page_cost and/or increasing cpu_tuple_cost."
    ///
    /// A consulta de contexto filtra por cliente. Com indice, o bloqueio de
    /// predicado cobre a faixa daquele cliente; com varredura sequencial,
    /// cobre a TABELA INTEIRA — e duas transacoes de clientes que nao tem nada
    /// a ver uma com a outra passam a conflitar.
    ///
    /// O padrao do PostgreSQL e 0.01. O valor daqui vale so dentro da
    /// transacao critica (<c>SET LOCAL</c>), e nao muda o planejamento de
    /// nenhuma outra consulta do sistema.
    /// </summary>
    public double CustoPorLinha { get; set; } = 1.0;

    public void Validar()
    {
        if (MaximoDeTentativas is < 1 or > 10)
        {
            throw new InvalidOperationException($"{Secao}:MaximoDeTentativas deve estar entre 1 e 10.");
        }

        if (EsperaBaseEmMs is < 0 or > 1_000)
        {
            throw new InvalidOperationException($"{Secao}:EsperaBaseEmMs deve estar entre 0 e 1000.");
        }

        if (EsperaMaximaEmMs < EsperaBaseEmMs || EsperaMaximaEmMs > 5_000)
        {
            throw new InvalidOperationException(
                $"{Secao}:EsperaMaximaEmMs deve ser maior ou igual a EsperaBaseEmMs e no maximo 5000.");
        }

        // O valor vai para dentro de um comando SQL. A faixa fechada e o que
        // garante que ele nao possa carregar nada alem de um numero.
        if (CustoPorLinha is < 0.01 or > 100 || !double.IsFinite(CustoPorLinha))
        {
            throw new InvalidOperationException($"{Secao}:CustoPorLinha deve estar entre 0,01 e 100.");
        }
    }
}
