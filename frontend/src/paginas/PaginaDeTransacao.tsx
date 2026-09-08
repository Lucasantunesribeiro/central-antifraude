import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from 'react-router';
import { requisitar } from '../api/clienteHttp';
import {
  ROTULO_DO_VEREDITO,
  type AlertaDaTransacao,
  type TransacaoDetalhada,
} from '../api/risco';
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

          {/*
            A faixa de metadados fica ACIMA das colunas, e nao dentro de uma
            delas: sao os dados de identificacao da transacao, e quem chega
            aqui quer confirmar que esta olhando a transacao certa antes de
            ler qualquer analise.

            Todo campo aqui e o que o produto REALMENTE guarda. Nao ha endereco
            IP, provedor nem confianca de dispositivo: o `CLAUDE.md` secao 57
            decidiu guardar apenas uma derivacao do IP, e a secao 58 proibe
            dado completo de pagamento. Mostrar o que nao se guarda seria
            anunciar uma capacidade que o produto nao tem.
          */}
          <dl className="faixa-meta">
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
            <div>
              {/* Número de protocolo da requisição que registrou esta
                  transação. É com ele que o suporte encontra a linha exata
                  no log do servidor (CLAUDE.md seção 69). */}
              <dt>Correlação</dt>
              <dd>
                <code>{consulta.data.idDeCorrelacao ?? '—'}</code>
              </dd>
            </div>
          </dl>

          {/*
            Duas colunas: a analise a esquerda, o que a operacao fez a direita.

            Sao perguntas diferentes — *por que esta decisao* e *alguem ja
            olhou isto* — e quem investiga precisa das duas na mesma tela, sem
            rolar de uma ate a outra.
          */}
          <div className="detalhe">
            <div className="detalhe__principal">
              {consulta.data.avaliacao ? (
                <>
                  <h2>
                    Por que esta transação recebeu {consulta.data.avaliacao.score}
                  </h2>
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
            </div>

            <aside className="detalhe__inspetor">
              <ContextoOperacional transacao={consulta.data} />
            </aside>
          </div>
        </>
      ) : null}
    </section>
  );
}

/**
 * O que a operação fez depois da avaliação.
 *
 * A avaliação responde *por que esta decisão*; isto responde *alguém já olhou
 * isto*. São perguntas diferentes, e quem investiga precisa das duas na mesma
 * tela — senão volta a procurar na outra aba.
 *
 * O veredito humano pode contradizer a decisão do motor: `Revisar` que termina
 * `Legítima` é um falso positivo legítimo, e não um defeito (CLAUDE.md
 * seção 11).
 */
function ContextoOperacional({ transacao }: { transacao: TransacaoDetalhada }) {
  if (transacao.alertas.length === 0 && transacao.veredito === null) {
    return null;
  }

  return (
    <>
      <h2>Operação</h2>

      {transacao.veredito ? (
        <div className="cartao">
          <p className="pagina__resumo">
            Investigação concluída como{' '}
            <strong>
              {ROTULO_DO_VEREDITO[transacao.veredito] ?? transacao.veredito}
            </strong>
            {transacao.vereditoRegistradoEm
              ? ` em ${new Date(transacao.vereditoRegistradoEm).toLocaleString('pt-BR')}`
              : ''}
            .{' '}
            {transacao.casoDoVeredito ? (
              <Link to={`/casos/${transacao.casoDoVeredito}`} className="ligacao">
                Ver o caso
              </Link>
            ) : null}
          </p>
          <p className="pagina__resumo">
            O resultado humano é diferente da decisão automática. Uma transação
            recomendada para revisão e concluída como legítima é um falso positivo — e é
            por isso que a investigação humana existe.
          </p>
        </div>
      ) : null}

      {transacao.alertas.length > 0 ? (
        <table className="tabela">
          <caption className="tabela__legenda">
            {transacao.alertas.length} alerta(s) desta transação
          </caption>
          <thead>
            <tr>
              <th scope="col">Criado em</th>
              <th scope="col">Prioridade</th>
              <th scope="col">Situação</th>
              <th scope="col">Caso</th>
            </tr>
          </thead>
          <tbody>
            {transacao.alertas.map((alerta) => (
              <LinhaDoAlerta key={alerta.alertaId} alerta={alerta} />
            ))}
          </tbody>
        </table>
      ) : null}
    </>
  );
}

function LinhaDoAlerta({ alerta }: { alerta: AlertaDaTransacao }) {
  return (
    <tr>
      <th scope="row">{new Date(alerta.criadoEm).toLocaleString('pt-BR')}</th>
      <td>{alerta.prioridade}</td>
      <td>{alerta.status}</td>
      <td>
        {alerta.casoId ? (
          <Link to={`/casos/${alerta.casoId}`} className="ligacao">
            {alerta.tituloDoCaso}
          </Link>
        ) : (
          'Sem caso'
        )}
      </td>
    </tr>
  );
}
