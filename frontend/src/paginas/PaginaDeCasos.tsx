import { useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link } from 'react-router';
import { requisitar } from '../api/clienteHttp';
import {
  consultaDeCasos,
  FILTROS_DE_CASOS_INICIAIS,
  RESULTADOS,
  ROTULO_DO_RESULTADO,
  ROTULO_DO_STATUS_DO_CASO,
  STATUS_DO_CASO,
  type CasoNaLista,
  type FiltrosDeCasos,
  type PaginaDeCasos as Pagina,
  type ResultadoDaInvestigacao,
  type StatusDoCaso,
} from '../api/casos';
import { ROTULO_DA_PRIORIDADE, idadeDoAlerta } from '../api/alertas';
import { Score } from '../componentes/Risco';
import {
  EstadoDeCarregamento,
  EstadoDeErro,
  EstadoVazio,
} from '../componentes/Estados';

/**
 * As investigações em andamento.
 *
 * A tela responde três perguntas operacionais, nessa ordem: **o que ninguém
 * pegou**, o que está em análise e o que já foi concluído. Por isso a ordem
 * padrão é por última movimentação, e não por abertura — um caso de uma semana
 * atrás que recebeu nota agora importa mais do que um caso aberto hoje e
 * intocado.
 */
export function PaginaDeCasos() {
  const [filtros, definirFiltros] = useState<FiltrosDeCasos>(FILTROS_DE_CASOS_INICIAIS);

  const consulta = useQuery({
    queryKey: ['casos', filtros],
    queryFn: ({ signal }) =>
      requisitar<Pagina>(`/api/casos?${consultaDeCasos(filtros)}`, { sinal: signal }),
    placeholderData: keepPreviousData,
  });

  function ajustar(mudanca: Partial<FiltrosDeCasos>) {
    definirFiltros((atual) => ({ ...atual, ...mudanca, pagina: 1 }));
  }

  const pagina = consulta.data;
  const filtrando =
    filtros.status !== '' || filtros.resultado !== '' || filtros.semResponsavel;

  return (
    <section className="pagina pagina--larga">
      <h1>Casos</h1>

      <form
        className="filtros"
        aria-label="Filtros dos casos"
        onSubmit={(evento) => evento.preventDefault()}
      >
        <label className="campo">
          <span className="campo__rotulo">Situação</span>
          <select
            value={filtros.status}
            onChange={(e) => ajustar({ status: e.target.value as StatusDoCaso | '' })}
          >
            <option value="">Todas</option>
            {STATUS_DO_CASO.map((status) => (
              <option key={status} value={status}>
                {ROTULO_DO_STATUS_DO_CASO[status]}
              </option>
            ))}
          </select>
        </label>

        <label className="campo">
          <span className="campo__rotulo">Resultado</span>
          <select
            value={filtros.resultado}
            onChange={(e) =>
              ajustar({ resultado: e.target.value as ResultadoDaInvestigacao | '' })
            }
          >
            <option value="">Todos</option>
            {RESULTADOS.map((resultado) => (
              <option key={resultado} value={resultado}>
                {ROTULO_DO_RESULTADO[resultado]}
              </option>
            ))}
          </select>
        </label>

        <label className="campo campo--caixa">
          <input
            type="checkbox"
            checked={filtros.semResponsavel}
            onChange={(e) => ajustar({ semResponsavel: e.target.checked })}
          />
          <span>Só os que ninguém pegou</span>
        </label>

        {filtrando ? (
          <button
            type="button"
            className="botao"
            onClick={() => definirFiltros(FILTROS_DE_CASOS_INICIAIS)}
          >
            Limpar filtros
          </button>
        ) : null}
      </form>

      {consulta.isPending ? (
        <EstadoDeCarregamento rotulo="Carregando casos..." />
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
            filtrando
              ? 'Nenhum caso com esses filtros.'
              : 'Nenhuma investigação aberta.'
          }
          descricao={
            filtrando
              ? 'Existem casos, mas nenhum atende ao que foi filtrado.'
              : 'Um caso nasce de um ou mais alertas. Abra a fila de alertas para começar.'
          }
        />
      ) : null}

      {pagina !== undefined && pagina.itens.length > 0 ? (
        <table className="tabela">
          <caption className="tabela__legenda">
            {pagina.total} caso(s){filtrando ? ' com os filtros aplicados' : ''}
          </caption>
          <thead>
            <tr>
              <th scope="col">Caso</th>
              <th scope="col">Situação</th>
              <th scope="col">Responsável</th>
              <th scope="col">Alertas</th>
              <th scope="col">Maior score</th>
              <th scope="col">Resultado</th>
              <th scope="col">Parado há</th>
            </tr>
          </thead>
          <tbody>
            {pagina.itens.map((caso) => (
              <LinhaDoCaso key={caso.id} caso={caso} />
            ))}
          </tbody>
        </table>
      ) : null}
    </section>
  );
}

function LinhaDoCaso({ caso }: { caso: CasoNaLista }) {
  return (
    <tr>
      <th scope="row">
        <Link to={`/casos/${caso.id}`} className="ligacao">
          {caso.titulo}
        </Link>
      </th>
      <td>
        <span className={`selo selo--caso-${caso.status.toLowerCase()}`}>
          {ROTULO_DO_STATUS_DO_CASO[caso.status]}
        </span>
      </td>
      <td>{caso.responsavelNome ?? <span className="vazio">ninguém</span>}</td>
      <td className="numerico">
        {caso.quantidadeDeAlertas}
        {caso.maiorPrioridade ? (
          <span
            className={`selo selo--prioridade-${caso.maiorPrioridade.toLowerCase()}`}
          >
            {ROTULO_DA_PRIORIDADE[caso.maiorPrioridade]}
          </span>
        ) : null}
      </td>
      <td>
        <Score valor={caso.maiorScore} />
      </td>
      <td>
        {caso.resultado ? (
          <span className={`selo selo--resultado-${caso.resultado.toLowerCase()}`}>
            {ROTULO_DO_RESULTADO[caso.resultado]}
          </span>
        ) : (
          <span className="vazio">—</span>
        )}
      </td>
      {/* Desde a última movimentação, e não desde a abertura: é isso que diz
          se um caso está esquecido. */}
      <td className="numerico">{idadeDoAlerta(caso.atualizadoEm)}</td>
    </tr>
  );
}
