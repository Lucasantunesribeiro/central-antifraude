import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from 'react-router';
import { requisitar } from '../api/clienteHttp';
import type { TransacaoDetalhada } from '../api/risco';
import { ListaDeSinais, ResumoDaAvaliacao } from '../componentes/Risco';
import {
  EstadoDeCarregamento,
  EstadoDeErro,
  EstadoVazio,
} from '../componentes/Estados';

/**
 * Detalhe de uma transação: o que chegou e por que recebeu aquele risco.
 *
 * A pergunta que esta tela responde é a do CLAUDE.md seção 17 — *por que esta
 * decisão foi tomada naquele momento?* Por isso ela mostra os sinais com a
 * contribuição de cada um, a versão do perfil e o instante da avaliação, e
 * não apenas o número final.
 *
 * O fingerprint de IP não aparece: o endereço bruto nunca chega ao banco
 * (seção 57), e o HMAC derivado dele não ajuda a investigação.
 */
export function PaginaDeTransacao() {
  const { id } = useParams<{ id: string }>();

  const consulta = useQuery({
    queryKey: ['transacoes', 'detalhe', id],
    queryFn: ({ signal }) =>
      requisitar<TransacaoDetalhada>(`/api/transacoes/${id}`, { sinal: signal }),
    enabled: Boolean(id),
  });

  return (
    <section className="pagina pagina--larga">
      <p className="pagina__migalha">
        <Link to="/transacoes" className="ligacao">
          ← Transações
        </Link>
      </p>

      {consulta.isPending ? (
        <EstadoDeCarregamento rotulo="Carregando transação..." />
      ) : null}

      {consulta.isError ? (
        <EstadoDeErro
          erro={consulta.error}
          aoTentarDeNovo={() => void consulta.refetch()}
        />
      ) : null}

      {consulta.isSuccess ? (
        <>
          <h1>
            <code>{consulta.data.identificadorExterno}</code>
          </h1>

          <div className="cartao">
            <dl className="descricao">
              <div>
                <dt>Cliente</dt>
                <dd>{consulta.data.clienteExternoId}</dd>
              </div>
              <div>
                <dt>Valor</dt>
                <dd className="numerico">
                  {consulta.data.valor.toLocaleString('pt-BR', {
                    style: 'currency',
                    currency: consulta.data.moeda,
                  })}
                </dd>
              </div>
              <div>
                <dt>Ocorrida em</dt>
                <dd>{new Date(consulta.data.ocorridaEm).toLocaleString('pt-BR')}</dd>
              </div>
              <div>
                <dt>Recebida em</dt>
                <dd>{new Date(consulta.data.recebidaEm).toLocaleString('pt-BR')}</dd>
              </div>
              <div>
                <dt>País</dt>
                <dd>{consulta.data.paisDeOrigem ?? '—'}</dd>
              </div>
              <div>
                <dt>Dispositivo</dt>
                <dd>
                  <code>{consulta.data.fingerprintDoDispositivo ?? '—'}</code>
                </dd>
              </div>
              <div>
                {/* Referência tokenizada, nunca o cartão: a Central
                    Antifraude não armazena dado completo de pagamento
                    (CLAUDE.md seção 58). */}
                <dt>Instrumento</dt>
                <dd>
                  <code>{consulta.data.referenciaDoInstrumento}</code>
                </dd>
              </div>
            </dl>
          </div>

          <h2>Avaliação de risco</h2>

          {consulta.data.avaliacao ? (
            <>
              <ResumoDaAvaliacao avaliacao={consulta.data.avaliacao} />
              <h2>Sinais</h2>
              <ListaDeSinais sinais={consulta.data.avaliacao.sinais} />
            </>
          ) : (
            <EstadoVazio
              titulo="Esta transação não tem avaliação."
              descricao="Ela foi registrada antes de o motor de risco existir. Avaliações não são recalculadas retroativamente: isso mudaria decisões históricas já congeladas."
            />
          )}
        </>
      ) : null}
    </section>
  );
}
