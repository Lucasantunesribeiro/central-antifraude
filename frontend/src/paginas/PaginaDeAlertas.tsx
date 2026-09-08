import { useState } from 'react';
import {
  keepPreviousData,
  useMutation,
  useQuery,
  useQueryClient,
} from '@tanstack/react-query';
import { Link, useNavigate } from 'react-router';
import { requisitar } from '../api/clienteHttp';
import {
  consultaDaFila,
  idadeDoAlerta,
  DECISOES_QUE_ALERTAM,
  FILTROS_INICIAIS,
  PRIORIDADES,
  ROTULO_DA_PRIORIDADE,
  ROTULO_DO_STATUS,
  SITUACOES,
  TAMANHO_DA_PAGINA,
  type Alerta,
  type CampoDeOrdenacao,
  type FiltrosDaFila,
  type PaginaDeAlertas as Pagina,
  type PrioridadeDeAlerta,
  type StatusDoAlerta,
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
  const [selecionados, definirSelecionados] = useState<readonly string[]>([]);

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
    // A selecao nao sobrevive a uma troca de filtro: manter marcados alertas
    // que sairam da tela abriria um caso com o que ninguem esta vendo.
    definirSelecionados([]);
  }

  function alternar(alertaId: string) {
    definirSelecionados((atual) =>
      atual.includes(alertaId)
        ? atual.filter((id) => id !== alertaId)
        : [...atual, alertaId],
    );
  }

  const pagina = consulta.data;
  const temAlertas = pagina !== undefined && pagina.itens.length > 0;
  const filtrando =
    filtros.decisao !== '' ||
    filtros.prioridade !== '' ||
    filtros.scoreMinimo !== '' ||
    filtros.status !== FILTROS_INICIAIS.status;

  return (
    <section className="pagina pagina--larga">
      <h1>Alertas</h1>

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
          <span className="campo__rotulo">Situação</span>
          <select
            value={filtros.status}
            onChange={(e) => ajustar({ status: e.target.value as StatusDoAlerta | '' })}
          >
            <option value="">Todas</option>
            {SITUACOES.map((situacao) => (
              <option key={situacao} value={situacao}>
                {ROTULO_DO_STATUS[situacao]}
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
          <AbrirCaso
            selecionados={selecionados}
            aoAbrir={() => definirSelecionados([])}
          />

          <table className="tabela">
            <caption className="tabela__legenda">
              {pagina.total} alerta(s){filtrando ? ' com os filtros aplicados' : ''}
            </caption>
            <thead>
              <tr>
                <th scope="col">
                  <span className="visualmente-oculto">Selecionar</span>
                </th>
                <th scope="col">Prioridade</th>
                <th scope="col">Decisão</th>
                <th scope="col">Score</th>
                <th scope="col">Sinais</th>
                <th scope="col">Idade</th>
                <th scope="col">Chegada</th>
                <th scope="col">Situação</th>
                <th scope="col">Transação</th>
              </tr>
            </thead>
            <tbody>
              {pagina.itens.map((alerta) => (
                <LinhaDoAlerta
                  key={alerta.id}
                  alerta={alerta}
                  selecionado={selecionados.includes(alerta.id)}
                  aoAlternar={() => alternar(alerta.id)}
                />
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

function LinhaDoAlerta({
  alerta,
  selecionado,
  aoAlternar,
}: {
  alerta: Alerta;
  selecionado: boolean;
  aoAlternar: () => void;
}) {
  const navegar = useNavigate();

  const escondidos = alerta.totalDeSinais - alerta.principaisSinais.length;

  return (
    /*
      A LINHA INTEIRA abre a transação.

      Numa fila de trabalho o alvo tem que ser a linha: mirar um link de seis
      letras no fim de nove colunas é o que fazia esta tela parecer relatório
      em vez de ferramenta.

      Três cuidados que isto exige, e que a versão ingênua erra:

      1. o clique não pode disparar quando a pessoa marcou a caixa de seleção
         ou clicou no próprio link — daí a checagem do alvo;
      2. um `<tr>` não é foco de teclado, e transformá-lo em botão exigiria
         reescrever a semântica da tabela. O link continua ali e continua sendo
         quem carrega o destino: quem navega por teclado chega por ele, e a
         linha é só um atalho de ponteiro;
      3. selecionar texto não pode navegar — por isso o clique é ignorado
         quando há seleção ativa.
    */
    <tr
      className="linha-clicavel"
      onClick={(evento) => {
        const alvo = evento.target as HTMLElement;

        if (alvo.closest('a, input, button, label')) {
          return;
        }

        if ((window.getSelection()?.toString().length ?? 0) > 0) {
          return;
        }

        navegar(`/transacoes/${alerta.transacaoId}`);
      }}
    >
      <td>
        {/* Só alerta na fila entra num caso novo. Um já investigado aparece
            sem caixa, e não com a caixa desabilitada: a ausência diz "isto não
            é trabalho seu" melhor do que um controle morto. */}
        {alerta.status === 'Aberto' ? (
          <input
            type="checkbox"
            checked={selecionado}
            aria-label={`Selecionar alerta de score ${alerta.score}`}
            onChange={aoAlternar}
          />
        ) : null}
      </td>
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
        {alerta.casoId ? (
          <Link to={`/casos/${alerta.casoId}`} className="ligacao">
            {ROTULO_DO_STATUS[alerta.status]}
          </Link>
        ) : (
          <span className="selo selo--alerta-aberto">
            {ROTULO_DO_STATUS[alerta.status]}
          </span>
        )}
      </td>
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

/**
 * A ponte entre a fila e a investigação.
 *
 * O caso nasce **dos alertas selecionados** (ROADMAP 7.3), e não de um
 * formulário vazio: um caso sem alerta não teria transação para investigar nem
 * resultado para registrar.
 */
function AbrirCaso({
  selecionados,
  aoAbrir,
}: {
  selecionados: readonly string[];
  aoAbrir: () => void;
}) {
  const [titulo, definirTitulo] = useState('');
  const [erro, definirErro] = useState<unknown>(null);
  const navegar = useNavigate();
  const filaDeConsultas = useQueryClient();

  const abertura = useMutation({
    mutationFn: () =>
      requisitar<{ id: string }>('/api/casos', {
        metodo: 'POST',
        corpo: { titulo, alertasIds: selecionados },
      }),
    onSuccess: async (caso) => {
      definirErro(null);
      definirTitulo('');
      aoAbrir();
      await filaDeConsultas.invalidateQueries({ queryKey: ['alertas'] });
      await navegar(`/casos/${caso.id}`);
    },
    onError: (falha) => definirErro(falha),
  });

  if (selecionados.length === 0) {
    return null;
  }

  return (
    <form
      className="formulario-em-linha barra-de-selecao"
      onSubmit={(evento) => {
        evento.preventDefault();
        abertura.mutate();
      }}
    >
      <span className="barra-de-selecao__contagem">
        {selecionados.length} alerta(s) selecionado(s)
      </span>

      <label className="campo">
        <span className="campo__rotulo">Título do caso</span>
        <input
          type="text"
          value={titulo}
          maxLength={120}
          placeholder="O que está sendo investigado"
          onChange={(e) => definirTitulo(e.target.value)}
        />
      </label>

      <button
        type="submit"
        className="botao botao--principal"
        disabled={abertura.isPending || titulo.trim().length < 3}
      >
        Abrir caso
      </button>

      {erro ? <EstadoDeErro erro={erro} /> : null}
    </form>
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
