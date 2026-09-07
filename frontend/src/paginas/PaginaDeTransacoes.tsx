import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router';
import { requisitar } from '../api/clienteHttp';
import {
  ROTULO_DO_TIPO_DE_REGRA,
  consultaDeTransacoes,
  type FiltroDeTransacoes,
  type TransacaoDaLista,
} from '../api/risco';
import { DECISOES } from '../api/operacao';
import { Score, SeloDeDecisao } from '../componentes/Risco';
import {
  EstadoDeCarregamento,
  EstadoDeErro,
  EstadoVazio,
} from '../componentes/Estados';

interface ListaDeTransacoes {
  itens: TransacaoDaLista[];
  pagina: number;
  tamanho: number;
  total: number;
  totalDePaginas: number;
}

const TAMANHO_DA_PAGINA = 25;

/**
 * Console de transações.
 *
 * Esta é a tela onde o analista começa o expediente, e onde ele volta quando
 * precisa achar uma transação específica. Por isso ela filtra, ordena e pagina
 * — tudo no servidor.
 *
 * **Nenhum filtro é aplicado aqui.** A tela monta a query string e o backend
 * decide: campo de ordenação vem de lista fechada, decisão e tipo de regra são
 * vocabulários fechados, e a busca vai como texto literal. Filtrar em memória
 * daria um total que não bate com a lista e páginas vazias no fim.
 *
 * O filtro inicial pode vir da URL — é o que faz "ver transações" do painel
 * cair aqui já filtrado, e o que torna um link de investigação compartilhável.
 */
export function PaginaDeTransacoes() {
  const [parametrosDaUrl] = useSearchParams();

  const [filtro, definirFiltro] = useState<FiltroDeTransacoes>(() => ({
    busca: parametrosDaUrl.get('busca') ?? '',
    decisao: parametrosDaUrl.get('decisao') ?? '',
    tipoDeRegra: parametrosDaUrl.get('tipoDeRegra') ?? '',
    scoreMinimo: parametrosDaUrl.get('scoreMinimo') ?? '',
    scoreMaximo: parametrosDaUrl.get('scoreMaximo') ?? '',
    de: '',
    ate: '',
  }));

  const [pagina, definirPagina] = useState(1);
  const [ordenarPor, definirOrdenarPor] = useState('recebidaEm');
  const [direcao, definirDirecao] = useState<'asc' | 'desc'>('desc');

  const consulta = consultaDeTransacoes(
    filtro,
    pagina,
    TAMANHO_DA_PAGINA,
    ordenarPor,
    direcao,
  );

  const lista = useQuery({
    queryKey: ['transacoes', 'lista', consulta],
    queryFn: ({ signal }) =>
      requisitar<ListaDeTransacoes>(`/api/transacoes?${consulta}`, { sinal: signal }),
  });

  function alterar(campo: keyof FiltroDeTransacoes, valor: string) {
    // Mudar o filtro volta para a primeira página: continuar na página 4 de um
    // resultado que agora tem uma página mostraria uma tela vazia e faria
    // parecer que não há nada.
    definirPagina(1);
    definirFiltro((atual) => ({ ...atual, [campo]: valor }));
  }

  function ordenarPorCampo(campo: string) {
    if (campo === ordenarPor) {
      definirDirecao((atual) => (atual === 'asc' ? 'desc' : 'asc'));
      return;
    }

    definirOrdenarPor(campo);
    definirDirecao('desc');
  }

  return (
    <section className="pagina pagina--larga">
      <h1>Transações</h1>
      <p className="pagina__resumo">
        Tentativas de pagamento recebidas das integrações desta organização, com o
        resultado do motor de risco. A decisão é uma recomendação — quem confirma fraude
        é a investigação humana.
      </p>

      <Filtros filtro={filtro} aoAlterar={alterar} />

      {lista.isPending ? (
        <EstadoDeCarregamento rotulo="Carregando transações..." />
      ) : null}

      {lista.isError ? (
        <EstadoDeErro erro={lista.error} aoTentarDeNovo={() => void lista.refetch()} />
      ) : null}

      {lista.isSuccess && lista.data.itens.length === 0 ? (
        <EstadoVazio
          titulo="Nenhuma transação encontrada."
          descricao="Ajuste os filtros ou envie uma tentativa de pagamento por uma integração."
        />
      ) : null}

      {lista.isSuccess && lista.data.itens.length > 0 ? (
        <>
          <Tabela
            lista={lista.data}
            ordenarPor={ordenarPor}
            direcao={direcao}
            aoOrdenar={ordenarPorCampo}
          />
          <Paginacao lista={lista.data} aoMudarPagina={definirPagina} />
        </>
      ) : null}
    </section>
  );
}

function Filtros({
  filtro,
  aoAlterar,
}: {
  filtro: FiltroDeTransacoes;
  aoAlterar: (campo: keyof FiltroDeTransacoes, valor: string) => void;
}) {
  return (
    <div className="filtros">
      <label className="campo">
        <span className="campo__rotulo">Buscar</span>
        <input
          type="search"
          value={filtro.busca ?? ''}
          maxLength={100}
          placeholder="Identificador ou cliente"
          onChange={(evento) => aoAlterar('busca', evento.target.value)}
        />
      </label>

      <label className="campo">
        <span className="campo__rotulo">Decisão</span>
        <select
          value={filtro.decisao ?? ''}
          onChange={(evento) => aoAlterar('decisao', evento.target.value)}
        >
          <option value="">Todas</option>
          {DECISOES.map((decisao) => (
            <option key={decisao} value={decisao}>
              {decisao}
            </option>
          ))}
        </select>
      </label>

      <label className="campo">
        <span className="campo__rotulo">Acionou a regra</span>
        <select
          value={filtro.tipoDeRegra ?? ''}
          onChange={(evento) => aoAlterar('tipoDeRegra', evento.target.value)}
        >
          <option value="">Qualquer</option>
          {Object.entries(ROTULO_DO_TIPO_DE_REGRA).map(([tipo, rotulo]) => (
            <option key={tipo} value={tipo}>
              {rotulo}
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
          value={filtro.scoreMinimo ?? ''}
          onChange={(evento) => aoAlterar('scoreMinimo', evento.target.value)}
        />
      </label>

      <label className="campo">
        <span className="campo__rotulo">Score máximo</span>
        <input
          type="number"
          min={0}
          max={100}
          value={filtro.scoreMaximo ?? ''}
          onChange={(evento) => aoAlterar('scoreMaximo', evento.target.value)}
        />
      </label>
    </div>
  );
}

function Tabela({
  lista,
  ordenarPor,
  direcao,
  aoOrdenar,
}: {
  lista: ListaDeTransacoes;
  ordenarPor: string;
  direcao: 'asc' | 'desc';
  aoOrdenar: (campo: string) => void;
}) {
  const cabecalho = (campo: string, rotulo: string) => (
    <CabecalhoOrdenavel
      campo={campo}
      rotulo={rotulo}
      ativo={campo === ordenarPor}
      direcao={direcao}
      aoOrdenar={aoOrdenar}
    />
  );

  return (
    <table className="tabela">
      <caption className="tabela__legenda">{lista.total} transação(ões)</caption>
      <thead>
        <tr>
          <th scope="col">Identificador</th>
          <th scope="col">Cliente</th>
          {cabecalho('valor', 'Valor')}
          {cabecalho('ocorridaEm', 'Ocorrida em')}
          {cabecalho('recebidaEm', 'Recebida em')}
          <th scope="col">País</th>
          {cabecalho('score', 'Score')}
          <th scope="col">Decisão</th>
        </tr>
      </thead>
      <tbody>
        {lista.itens.map((transacao) => (
          <tr key={transacao.id}>
            <th scope="row">
              <Link to={`/transacoes/${transacao.id}`} className="ligacao">
                <code>{transacao.identificadorExterno}</code>
              </Link>
            </th>
            <td>{transacao.clienteExternoId}</td>
            <td className="numerico">
              {transacao.valor.toLocaleString('pt-BR', {
                style: 'currency',
                currency: transacao.moeda,
              })}
            </td>
            <td>{new Date(transacao.ocorridaEm).toLocaleString('pt-BR')}</td>
            <td>{new Date(transacao.recebidaEm).toLocaleString('pt-BR')}</td>
            <td>{transacao.paisDeOrigem ?? '—'}</td>
            <td>
              <Score valor={transacao.score} />
            </td>
            <td>
              <SeloDeDecisao decisao={transacao.decisao} />
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/**
 * Cabeçalho que ordena a coluna.
 *
 * `aria-sort` existe porque a setinha só comunica a ordem a quem enxerga —
 * numa tabela operacional, saber por qual coluna a lista está ordenada muda a
 * leitura de tudo o que vem abaixo.
 */
function CabecalhoOrdenavel({
  campo,
  rotulo,
  ativo,
  direcao,
  aoOrdenar,
}: {
  campo: string;
  rotulo: string;
  ativo: boolean;
  direcao: 'asc' | 'desc';
  aoOrdenar: (campo: string) => void;
}) {
  const ordem = !ativo ? 'none' : direcao === 'asc' ? 'ascending' : 'descending';

  return (
    <th scope="col" aria-sort={ordem}>
      <button type="button" className="botao" onClick={() => aoOrdenar(campo)}>
        {rotulo}
        {ativo ? (direcao === 'asc' ? ' ↑' : ' ↓') : ''}
      </button>
    </th>
  );
}

function Paginacao({
  lista,
  aoMudarPagina,
}: {
  lista: ListaDeTransacoes;
  aoMudarPagina: (pagina: number) => void;
}) {
  if (lista.totalDePaginas <= 1) {
    return null;
  }

  return (
    <nav className="paginacao" aria-label="Paginação das transações">
      <button
        type="button"
        className="botao"
        disabled={lista.pagina <= 1}
        onClick={() => aoMudarPagina(lista.pagina - 1)}
      >
        Anterior
      </button>

      <span className="paginacao__posicao">
        Página {lista.pagina} de {lista.totalDePaginas}
      </span>

      <button
        type="button"
        className="botao"
        disabled={lista.pagina >= lista.totalDePaginas}
        onClick={() => aoMudarPagina(lista.pagina + 1)}
      >
        Próxima
      </button>
    </nav>
  );
}
