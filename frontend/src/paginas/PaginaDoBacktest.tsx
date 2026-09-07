import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router';
import { requisitar } from '../api/clienteHttp';
import {
  ROTULO_DO_STATUS,
  direcaoDaMudanca,
  estaEmAndamento,
  podeCancelar,
  rotularVeredito,
  variacao,
  type BacktestDetalhado,
  type Distribuicao,
  type ResultadoDoBacktest,
} from '../api/backtests';
import { rotularTipoDeRegra } from '../api/risco';
import { EstadoDeCarregamento, EstadoDeErro } from '../componentes/Estados';

/**
 * O resultado de uma simulação.
 *
 * **Três leituras, e a ordem é deliberada.** Primeiro o que muda — é a
 * pergunta que o Supervisor veio fazer. Depois o que a investigação humana
 * apurou sobre as mesmas transações, com o denominador visível. Por último a
 * distribuição de score, que é contexto.
 *
 * Nenhum número aqui é chamado de precisão ou recall. A maioria das
 * transações nunca foi investigada, e as que foram não são amostra aleatória:
 * uma taxa calculada sobre isso seria uma afirmação que os dados não
 * sustentam (ROADMAP 9.6).
 */
export function PaginaDoBacktest() {
  const { id } = useParams();

  const consulta = useQuery({
    queryKey: ['backtests', id],
    enabled: id !== undefined,
    queryFn: ({ signal }) =>
      requisitar<BacktestDetalhado>(`/api/backtests/${id}`, { sinal: signal }),
    refetchInterval: (atual) =>
      atual.state.data && estaEmAndamento(atual.state.data.execucao.status)
        ? 2_000
        : false,
  });

  if (consulta.isPending) {
    return <EstadoDeCarregamento rotulo="Carregando simulação..." />;
  }

  if (consulta.isError) {
    return (
      <EstadoDeErro
        erro={consulta.error}
        aoTentarDeNovo={() => void consulta.refetch()}
      />
    );
  }

  const { execucao, candidato, resultado } = consulta.data;

  return (
    <section className="pagina pagina--larga">
      <p className="pagina__migalha">
        <Link to="/backtests" className="ligacao">
          Backtests
        </Link>
      </p>

      <header>
        <h1>{execucao.descricao}</h1>
      </header>

      <p className="pagina__resumo">
        {ROTULO_DO_STATUS[execucao.status]} · período de{' '}
        {new Date(execucao.inicio).toLocaleDateString('pt-BR')} a{' '}
        {new Date(execucao.fim).toLocaleDateString('pt-BR')} · comparado com o perfil
        versão {execucao.numeroDaVersaoDePerfilVigente} · pedido por{' '}
        {execucao.solicitadaPor}
      </p>

      {execucao.mensagemDeErro ? (
        <p className="pagina__resumo" role="alert">
          {execucao.mensagemDeErro}
        </p>
      ) : null}

      <Acoes execucao={execucao} />

      <Candidato candidato={candidato} />

      {resultado ? (
        <Resultado resultado={resultado} />
      ) : (
        <p className="pagina__resumo">
          {estaEmAndamento(execucao.status)
            ? 'A execução ainda não terminou. Esta página se atualiza sozinha.'
            : 'Esta execução não produziu resultado.'}
        </p>
      )}
    </section>
  );
}

function Acoes({ execucao }: { execucao: BacktestDetalhado['execucao'] }) {
  const filaDeConsultas = useQueryClient();

  const cancelar = useMutation({
    mutationFn: () =>
      requisitar<BacktestDetalhado>(`/api/backtests/${execucao.id}/cancelamento`, {
        metodo: 'POST',
        // A versão lida vai junto: é ela que impede cancelar sobre uma tela
        // velha, e é ela que faz o worker perder a corrida se estiver
        // terminando agora.
        corpo: { versao: execucao.versao },
      }),
    onSuccess: async () => {
      await filaDeConsultas.invalidateQueries({ queryKey: ['backtests'] });
    },
  });

  if (!podeCancelar(execucao.status)) {
    return null;
  }

  return (
    <>
      {cancelar.isError ? <EstadoDeErro erro={cancelar.error} /> : null}

      <div className="acoes acoes--caso">
        <button
          type="button"
          className="botao"
          disabled={cancelar.isPending}
          onClick={() => cancelar.mutate()}
        >
          Cancelar execução
        </button>
      </div>
    </>
  );
}

/**
 * O perfil candidato, congelado no momento do pedido.
 *
 * Mostrado por inteiro de propósito: o Supervisor precisa ver que o resultado
 * fala do rascunho de então, e não da configuração que ele possa ter editado
 * depois.
 */
function Candidato({ candidato }: { candidato: BacktestDetalhado['candidato'] }) {
  return (
    <div className="cartao">
      <h2>Perfil candidato</h2>

      <p className="pagina__resumo">
        Revisão a partir de {candidato.limiarDeRevisao}, bloqueio a partir de{' '}
        {candidato.limiarDeBloqueio}. Esta é a composição congelada quando a simulação
        foi pedida — editar o rascunho depois não muda este resultado.
      </p>

      <table className="tabela">
        <caption className="tabela__legenda">
          {candidato.regras.length} regra(s) no candidato
        </caption>
        <thead>
          <tr>
            <th scope="col">Regra</th>
            <th scope="col">Tipo</th>
            <th scope="col">Configuração</th>
            <th scope="col">Pontos</th>
            <th scope="col">Origem</th>
          </tr>
        </thead>
        <tbody>
          {candidato.regras.map((regra) => (
            <tr key={regra.regraId}>
              <th scope="row">{regra.nome}</th>
              <td>{rotularTipoDeRegra(regra.tipo)}</td>
              <td>{regra.configuracao}</td>
              <td className="numerico">+{regra.pontos}</td>
              <td>
                {regra.origem === 'Rascunho' ? 'Rascunho (a mudança)' : 'Em vigor'}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Resultado({ resultado }: { resultado: ResultadoDoBacktest }) {
  return (
    <>
      <div className="cartao">
        <h2>O que mudaria</h2>

        <p className="pagina__resumo">
          {resultado.totalAnalisado} transação(ões) analisada(s);{' '}
          {resultado.totalQueAcionaria} acionaria(m) ao menos uma regra do candidato;{' '}
          {resultado.totalDeMudancas} teria(m) decisão diferente.
        </p>

        <TabelaDeDecisoes vigente={resultado.vigente} candidato={resultado.candidato} />

        {resultado.mudancas.length > 0 ? (
          <table className="tabela">
            <caption className="tabela__legenda">Transições de decisão</caption>
            <thead>
              <tr>
                <th scope="col">De</th>
                <th scope="col">Para</th>
                <th scope="col">Direção</th>
                <th scope="col">Transações</th>
              </tr>
            </thead>
            <tbody>
              {resultado.mudancas.map((mudanca) => (
                <tr key={`${mudanca.de}-${mudanca.para}`}>
                  <th scope="row">{mudanca.de}</th>
                  <td>{mudanca.para}</td>
                  <td>
                    {direcaoDaMudanca(mudanca) === 'mais-rigido'
                      ? 'Mais rígido'
                      : 'Mais permissivo'}
                  </td>
                  <td className="numerico">{mudanca.quantidade}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : (
          <p className="pagina__resumo">Nenhuma decisão mudaria neste período.</p>
        )}
      </div>

      <div className="cartao">
        <h2>Cruzamento com a investigação humana</h2>

        <p className="pagina__resumo">
          Contagens brutas, com o denominador de cada linha. Não são taxa de acerto: a
          maioria das transações nunca foi investigada, e as que foram não são uma
          amostra aleatória.
        </p>

        <table className="tabela">
          <thead>
            <tr>
              <th scope="col">Resultado apurado</th>
              <th scope="col">Transações</th>
              <th scope="col">Permitir</th>
              <th scope="col">Revisar</th>
              <th scope="col">Bloquear</th>
            </tr>
          </thead>
          <tbody>
            {resultado.porVeredito.map((linha) => (
              <tr key={linha.veredito ?? 'sem-investigacao'}>
                <th scope="row">{rotularVeredito(linha.veredito)}</th>
                <td className="numerico">{linha.total}</td>
                <td className="numerico">
                  {linha.candidato.permitir}{' '}
                  <Variacao
                    vigente={linha.vigente.permitir}
                    candidato={linha.candidato.permitir}
                  />
                </td>
                <td className="numerico">
                  {linha.candidato.revisar}{' '}
                  <Variacao
                    vigente={linha.vigente.revisar}
                    candidato={linha.candidato.revisar}
                  />
                </td>
                <td className="numerico">
                  {linha.candidato.bloquear}{' '}
                  <Variacao
                    vigente={linha.vigente.bloquear}
                    candidato={linha.candidato.bloquear}
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="cartao">
        <h2>Distribuição de score</h2>

        <table className="tabela">
          <thead>
            <tr>
              <th scope="col">Faixa</th>
              <th scope="col">Perfil em vigor</th>
              <th scope="col">Candidato</th>
            </tr>
          </thead>
          <tbody>
            {resultado.faixasDeScore.map((faixa) => (
              <tr key={faixa.de}>
                <th scope="row">
                  {faixa.de} a {faixa.ate}
                </th>
                <td className="numerico">{faixa.vigente}</td>
                <td className="numerico">{faixa.candidato}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

function TabelaDeDecisoes({
  vigente,
  candidato,
}: {
  vigente: Distribuicao;
  candidato: Distribuicao;
}) {
  const linhas = [
    { rotulo: 'Permitir', vigente: vigente.permitir, candidato: candidato.permitir },
    { rotulo: 'Revisar', vigente: vigente.revisar, candidato: candidato.revisar },
    { rotulo: 'Bloquear', vigente: vigente.bloquear, candidato: candidato.bloquear },
  ];

  return (
    <table className="tabela">
      <caption className="tabela__legenda">Decisões, lado a lado</caption>
      <thead>
        <tr>
          <th scope="col">Decisão</th>
          <th scope="col">Perfil em vigor</th>
          <th scope="col">Candidato</th>
          <th scope="col">Diferença</th>
        </tr>
      </thead>
      <tbody>
        {linhas.map((linha) => (
          <tr key={linha.rotulo}>
            <th scope="row">{linha.rotulo}</th>
            <td className="numerico">{linha.vigente}</td>
            <td className="numerico">{linha.candidato}</td>
            <td className="numerico">
              {variacao(linha.vigente, linha.candidato) || '—'}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** Diferença entre os dois perfis. Vazia quando não há diferença. */
function Variacao({ vigente, candidato }: { vigente: number; candidato: number }) {
  const texto = variacao(vigente, candidato);

  return texto === '' ? null : <span className="pagina__resumo">({texto})</span>;
}
