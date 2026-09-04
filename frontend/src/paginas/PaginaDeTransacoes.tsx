import { useQuery } from '@tanstack/react-query';
import { requisitar } from '../api/clienteHttp';
import {
  EstadoDeCarregamento,
  EstadoDeErro,
  EstadoVazio,
} from '../componentes/Estados';

interface Transacao {
  id: string;
  identificadorExterno: string;
  valor: number;
  moeda: string;
  ocorridaEm: string;
  recebidaEm: string;
  clienteExternoId: string;
  paisDeOrigem: string | null;
}

interface ListaDeTransacoes {
  itens: Transacao[];
  total: number;
}

/**
 * Transações recebidas.
 *
 * Ainda não é a tela operacional do produto: não há score, sinais nem
 * decisão — isso chega na Fase 3, junto do motor de risco. O que existe aqui
 * é a confirmação de que a ingestão está funcionando e o registro do que
 * chegou.
 *
 * A coluna "atraso" já aparece porque é a diferença entre os dois tempos que
 * a Fase 4 vai usar para tratar evento atrasado.
 */
export function PaginaDeTransacoes() {
  const consulta = useQuery({
    queryKey: ['transacoes', 'lista'],
    queryFn: ({ signal }) =>
      requisitar<ListaDeTransacoes>('/api/transacoes?tamanho=50', { sinal: signal }),
  });

  return (
    <section className="pagina pagina--larga">
      <h1>Transações</h1>
      <p className="pagina__resumo">
        Tentativas de pagamento recebidas das integrações desta organização.
      </p>

      {consulta.isPending ? (
        <EstadoDeCarregamento rotulo="Carregando transações..." />
      ) : null}

      {consulta.isError ? (
        <EstadoDeErro
          erro={consulta.error}
          aoTentarDeNovo={() => void consulta.refetch()}
        />
      ) : null}

      {consulta.isSuccess && consulta.data.itens.length === 0 ? (
        <EstadoVazio
          titulo="Nenhuma transação recebida."
          descricao="Configure uma integração e envie a primeira tentativa de pagamento."
        />
      ) : null}

      {consulta.isSuccess && consulta.data.itens.length > 0 ? (
        <table className="tabela">
          <caption className="tabela__legenda">
            {consulta.data.total} transação(ões)
          </caption>
          <thead>
            <tr>
              <th scope="col">Identificador</th>
              <th scope="col">Cliente</th>
              <th scope="col">Valor</th>
              <th scope="col">Ocorrida em</th>
              <th scope="col">Atraso</th>
              <th scope="col">País</th>
            </tr>
          </thead>
          <tbody>
            {consulta.data.itens.map((transacao) => (
              <tr key={transacao.id}>
                <th scope="row">
                  <code>{transacao.identificadorExterno}</code>
                </th>
                <td>{transacao.clienteExternoId}</td>
                <td className="numerico">
                  {transacao.valor.toLocaleString('pt-BR', {
                    style: 'currency',
                    currency: transacao.moeda,
                  })}
                </td>
                <td>{new Date(transacao.ocorridaEm).toLocaleString('pt-BR')}</td>
                <td className="numerico">{formatarAtraso(transacao)}</td>
                <td>{transacao.paisDeOrigem ?? '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
    </section>
  );
}

/**
 * Quanto tempo a transação levou entre acontecer na origem e chegar aqui.
 *
 * Os dois tempos são distintos por decisão de domínio: uma transação que
 * chega com horas de atraso não pode ser tratada como se tivesse acabado de
 * acontecer.
 */
function formatarAtraso(transacao: Transacao): string {
  const atrasoEmMs =
    new Date(transacao.recebidaEm).getTime() - new Date(transacao.ocorridaEm).getTime();

  const segundos = Math.round(atrasoEmMs / 1000);

  if (segundos < 60) {
    return `${segundos}s`;
  }

  if (segundos < 3600) {
    return `${Math.round(segundos / 60)}min`;
  }

  return `${(segundos / 3600).toFixed(1)}h`;
}
