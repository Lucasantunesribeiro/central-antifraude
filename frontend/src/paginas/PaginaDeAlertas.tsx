import { useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link } from 'react-router';
import { requisitar } from '../api/clienteHttp';
import {
  consultaDaFila,
  idadeDoAlerta,
  DECISOES_QUE_ALERTAM,
  FILTROS_INICIAIS,
  PRIORIDADES,
  ROTULO_DA_PRIORIDADE,
  TAMANHO_DA_PAGINA,
  type Alerta,
  type CampoDeOrdenacao,
  type FiltrosDaFila,
  type PaginaDeAlertas as Pagina,
  type PrioridadeDeAlerta,
} from '../api/alertas';
import { ROTULO_DA_DECISAO, rotularTipoDeRegra, type Decisao } from '../api/risco';
import { Score, SeloDeDecisao } from '../componentes/Risco';
import {
  EstadoDeCarregamento,
  EstadoDeErro,
  EstadoVazio,
} from '../componentes/Estados';

/**
 * A fila de trabalho do analista.
 *
 * Esta é a tela onde o expediente começa, e ela é uma **fila**, não um
 * painel: linhas densas, ordenadas, filtráveis, com o caminho para o caso
 * concreto a um clique. Sem cartões decorativos e sem gráfico — o analista
 * precisa saber o que pegar primeiro, não quantos alertas houve no trimestre.
 *
 * Filtro, ordenação e paginação acontecem **no servidor**. Trazer a fila
 * inteira e filtrar aqui funcionaria com trinta alertas e quebraria com trinta
 * mil — e, pior, entregaria ao navegador alertas que o analista pediu para não
 * ver.
 */
export function PaginaDeAlertas() {
  const [filtros, definirFiltros] = useState<FiltrosDaFila>(FILTROS_INICIAIS);

  const consulta = useQuery({
    queryKey: ['alertas', filtros],
    queryFn: ({ signal }) =>
      requisitar<Pagina>(`/api/alertas?${consultaDaFila(filtros)}`, { sinal: signal }),
    // Mantém a página anterior visível enquanto a nova carrega: sem isso, cada
    // troca de filtro pisca a tabela inteira e o analista perde a linha que
    // estava lendo.
    placeholderData: keepPreviousData,
  });

  /** Muda um filtro e volta para a primeira página. */
  function ajustar(mudanca: Partial<FiltrosDaFila>) {
    definirFiltros((atual) => ({ ...atual, ...mudanca, pagina: 1 }));
  }

  const pagina = consulta.data;
  const temAlertas = pagina !== undefined && pagina.itens.length > 0;
  const filtrando =
    filtros.decisao !== '' || filtros.prioridade !== '' || filtros.scoreMinimo !== '';

  return (
    <section className="pagina pagina--larga">
      <h1>Alertas</h1>
      <p className="pagina__resumo">
        Avaliações que o motor recomendou revisar ou bloquear. A prioridade vem da
        política de alertas — quem confirma fraude é a investigação humana.
      </p>

      <form
        className="filtros"
        aria-label="Filtros da fila"
        onSubmit={(evento) => evento.preventDefault()}
      >
        <label className="campo">
          <span className="campo__rotulo">Prioridade</span>
          <select
            value={filtros.prioridade}
            onChange={(e) =>
              ajustar({ prioridade: e.target.value as PrioridadeDeAlerta | '' })
            }
          >
            <option value="">Todas</option>
            {PRIORIDADES.map((prioridade) => (
              <option key={prioridade} value={prioridade}>
                {ROTULO_DA_PRIORIDADE[prioridade]}
              </option>
            ))}
          </select>
        </label>

        <label className="campo">
          <span className="campo__rotulo">Decisão</span>
          <select
            value={filtros.decisao}
            onChange={(e) => ajustar({ decisao: e.target.value as Decisao | '' })}
          >
            <option value="">Todas</option>
            {DECISOES_QUE_ALERTAM.map((decisao) => (
              <option key={decisao} value={decisao}>
                {ROTULO_DA_DECISAO[decisao]}
              </option>
            ))}
          </select>
        </label>

        <label className="campo">
          <span className="campo__rotulo">Score mínimo</span>
          <input
            type="number"
            min={0}
            max={100}
            value={filtros.scoreMinimo}
            onChange={(e) => ajustar({ scoreMinimo: e.target.value })}
          />
        </label>

        <label className="campo">
          <span className="campo__rotulo">Ordenar por</span>
          <select
            value={filtros.ordenarPor}
            onChange={(e) =>
              ajustar({ ordenarPor: e.target.value as CampoDeOrdenacao })
            }
          >
            <option value="criadoEm">Chegada</option>
            <option value="prioridade">Prioridade</option>
            <option value="score">Score</option>
          </select>
        </label>

        <label className="campo">
          <span className="campo__rotulo">Direção</span>
          <select
            value={filtros.direcao}
            onChange={(e) => ajustar({ direcao: e.target.value as 'asc' | 'desc' })}
          >
            <option value="desc">Maior primeiro</option>
            <option value="asc">Menor primeiro</option>
          </select>
        </label>

        {filtrando ? (
          <button
            type="button"
            className="botao"
            onClick={() => definirFiltros(FILTROS_INICIAIS)}
          >
            Limpar filtros
          </button>
        ) : null}
      </form>

      {consulta.isPending ? (
        <EstadoDeCarregamento rotulo="Carregando alertas..." />
      ) : null}

      {consulta.isError ? (
        <EstadoDeErro
          erro={consulta.error}
          aoTentarDeNovo={() => void consulta.refetch()}
        />
      ) : null}

      {pagina !== undefined && pagina.itens.length === 0 ? (
        <EstadoVazio
          titulo={
            filtrando ? 'Nenhum alerta com esses filtros.' : 'Nenhum alerta na fila.'
          }
          descricao={
            filtrando
              ? 'A fila tem alertas, mas nenhum atende ao que foi filtrado.'
              : 'Transações permitidas não geram alerta. A fila enche quando o motor recomenda revisar ou bloquear.'
          }
        />
      ) : null}

      {temAlertas ? (
        <>
          <table className="tabela">
            <caption className="tabela__legenda">
              {pagina.total} alerta(s){filtrando ? ' com os filtros aplicados' : ''}
            </caption>
            <thead>
              <tr>
                <th scope="col">Prioridade</th>
                <th scope="col">Decisão</th>
                <th scope="col">Score</th>
                <th scope="col">Sinais</th>
                <th scope="col">Idade</th>
                <th scope="col">Chegada</th>
                <th scope="col">Transação</th>
              </tr>
            </thead>
            <tbody>
              {pagina.itens.map((alerta) => (
                <LinhaDoAlerta key={alerta.id} alerta={alerta} />
              ))}
            </tbody>
          </table>

          <Paginacao
            pagina={pagina}
            aoMudar={(numero) =>
              definirFiltros((atual) => ({ ...atual, pagina: numero }))
            }
          />
        </>
      ) : null}
    </section>
  );
}

function LinhaDoAlerta({ alerta }: { alerta: Alerta }) {
  const escondidos = alerta.totalDeSinais - alerta.principaisSinais.length;

  return (
    <tr>
      <th scope="row">
        <span className={`selo selo--prioridade-${alerta.prioridade.toLowerCase()}`}>
          {ROTULO_DA_PRIORIDADE[alerta.prioridade]}
        </span>
      </th>
      <td>
        <SeloDeDecisao decisao={alerta.decisao} />
      </td>
      <td>
        <Score valor={alerta.score} />
      </td>
      <td>
        <ul className="sinais-resumidos">
          {alerta.principaisSinais.map((sinal) => (
            <li key={sinal.tipo}>
              {rotularTipoDeRegra(sinal.tipo)}{' '}
              <span className="numerico">+{sinal.pontos}</span>
            </li>
          ))}
          {/* Sem isto, três sinais numa avaliação de cinco fariam o analista
              acreditar que viu tudo o que pesou. */}
          {escondidos > 0 ? (
            <li className="sinais-resumidos__resto">e mais {escondidos}</li>
          ) : null}
        </ul>
      </td>
      <td className="numerico">{idadeDoAlerta(alerta.criadoEm)}</td>
      <td>{new Date(alerta.criadoEm).toLocaleString('pt-BR')}</td>
      <td>
        {/* A navegação do alerta para o caso concreto: a transação, a
            avaliação e os sinais completos, com explicação e versão de regra. */}
        <Link to={`/transacoes/${alerta.transacaoId}`} className="ligacao">
          Abrir transação
        </Link>
      </td>
    </tr>
  );
}

function Paginacao({
  pagina,
  aoMudar,
}: {
  pagina: Pagina;
  aoMudar: (numero: number) => void;
}) {
  if (pagina.totalDePaginas <= 1) {
    return null;
  }

  const primeiro = (pagina.pagina - 1) * TAMANHO_DA_PAGINA + 1;
  const ultimo = Math.min(pagina.pagina * TAMANHO_DA_PAGINA, pagina.total);

  return (
    <nav className="paginacao" aria-label="Paginação da fila">
      <button
        type="button"
        className="botao"
        disabled={pagina.pagina <= 1}
        onClick={() => aoMudar(pagina.pagina - 1)}
      >
        Anterior
      </button>

      <span className="paginacao__posicao">
        {primeiro}–{ultimo} de {pagina.total}
      </span>

      <button
        type="button"
        className="botao"
        disabled={pagina.pagina >= pagina.totalDePaginas}
        onClick={() => aoMudar(pagina.pagina + 1)}
      >
        Próxima
      </button>
    </nav>
  );
}
