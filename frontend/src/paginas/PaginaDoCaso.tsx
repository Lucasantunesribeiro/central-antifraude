import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router';
import { requisitar } from '../api/clienteHttp';
import {
  permite,
  RESULTADOS,
  ROTULO_DO_RESULTADO,
  ROTULO_DO_STATUS_DO_CASO,
  type AlertaDoCaso,
  type CasoDetalhado,
  type EventoDoCaso,
  type NotaDoCaso,
  type ResultadoDaInvestigacao,
} from '../api/casos';
import { ROTULO_DA_PRIORIDADE } from '../api/alertas';
import { rotularTipoDeRegra } from '../api/risco';
import { Score, SeloDeDecisao } from '../componentes/Risco';
import { EstadoDeCarregamento, EstadoDeErro } from '../componentes/Estados';

/**
 * O workspace da investigação.
 *
 * Reúne, numa tela só, tudo que o analista precisa para concluir: o risco, as
 * transações, os sinais que pesaram, a história do caso e as notas. É a tela
 * de maior prioridade visual da fase — se ela obrigar a abrir outra aba, ela
 * falhou.
 *
 * **As ações vêm do servidor.** A tela não deduz quem pode assumir, transferir
 * ou resolver: ela mostra o que o backend listou em `acoesPermitidas`, e o
 * backend recusa de novo quando a ação chega. Esconder botão não é
 * autorização.
 *
 * **Toda ação envia a versão que a tela leu.** É assim que duas pessoas
 * trabalhando no mesmo caso não escrevem uma por cima da outra: quem age com
 * informação velha recebe um conflito, e não um sucesso silencioso.
 */
export function PaginaDoCaso() {
  const { id = '' } = useParams();
  const filaDeConsultas = useQueryClient();
  const [erroDaAcao, definirErroDaAcao] = useState<unknown>(null);

  const consulta = useQuery({
    queryKey: ['caso', id],
    queryFn: ({ signal }) =>
      requisitar<CasoDetalhado>(`/api/casos/${id}`, { sinal: signal }),
  });

  const acao = useMutation({
    mutationFn: ({ caminho, corpo }: { caminho: string; corpo: unknown }) =>
      requisitar<unknown>(`/api/casos/${id}/${caminho}`, {
        metodo: 'POST',
        corpo,
      }),
    onSuccess: async () => {
      definirErroDaAcao(null);
      await filaDeConsultas.invalidateQueries({ queryKey: ['caso', id] });
      await filaDeConsultas.invalidateQueries({ queryKey: ['casos'] });
      await filaDeConsultas.invalidateQueries({ queryKey: ['alertas'] });
    },
    // O erro fica na tela em vez de sumir: um conflito de versão precisa ser
    // lido, e não engolido.
    onError: (erro) => definirErroDaAcao(erro),
  });

  if (consulta.isPending) {
    return <EstadoDeCarregamento rotulo="Carregando o caso..." />;
  }

  if (consulta.isError) {
    return (
      <EstadoDeErro
        erro={consulta.error}
        aoTentarDeNovo={() => void consulta.refetch()}
      />
    );
  }

  const caso = consulta.data;

  return (
    <section className="pagina pagina--larga">
      <p className="pagina__migalha">
        <Link to="/casos" className="ligacao">
          Casos
        </Link>
      </p>

      <Cabecalho caso={caso} />

      {erroDaAcao ? <EstadoDeErro erro={erroDaAcao} /> : null}

      <Acoes
        caso={caso}
        ocupado={acao.isPending}
        executar={(caminho, corpo) => acao.mutate({ caminho, corpo })}
      />

      <h2>Alertas e transações</h2>
      <AlertasDoCaso alertas={caso.alertas} />

      <div className="colunas">
        <div>
          <h2>Notas de investigação</h2>
          <Notas
            caso={caso}
            ocupado={acao.isPending}
            executar={(caminho, corpo) => acao.mutate({ caminho, corpo })}
          />
        </div>

        <div>
          <h2>Histórico</h2>
          <Timeline eventos={caso.timeline} />
        </div>
      </div>
    </section>
  );
}

function Cabecalho({ caso }: { caso: CasoDetalhado }) {
  return (
    <>
      <h1>{caso.titulo}</h1>

      <div className="cartao">
        <dl className="descricao">
          <div>
            <dt>Situação</dt>
            <dd>
              <span className={`selo selo--caso-${caso.status.toLowerCase()}`}>
                {ROTULO_DO_STATUS_DO_CASO[caso.status]}
              </span>
            </dd>
          </div>
          <div>
            <dt>Responsável</dt>
            <dd>{caso.responsavelNome ?? <span className="vazio">ninguém</span>}</dd>
          </div>
          <div>
            <dt>Aberto por</dt>
            <dd>{caso.abertoPorNome ?? '—'}</dd>
          </div>
          <div>
            <dt>Aberto em</dt>
            <dd>{new Date(caso.abertoEm).toLocaleString('pt-BR')}</dd>
          </div>
          {caso.resultado ? (
            <>
              <div>
                <dt>Resultado</dt>
                <dd>
                  <span
                    className={`selo selo--resultado-${caso.resultado.toLowerCase()}`}
                  >
                    {ROTULO_DO_RESULTADO[caso.resultado]}
                  </span>
                </dd>
              </div>
              <div>
                <dt>Resolvido por</dt>
                <dd>{caso.resolvidoPorNome ?? '—'}</dd>
              </div>
            </>
          ) : null}
        </dl>

        {caso.status === 'Resolvido' ? (
          <p className="avaliacao__aviso">
            Este caso está resolvido e não muda mais. Uma conclusão diferente exige um
            caso novo — o histórico do que já foi decidido não é reescrito.
          </p>
        ) : null}
      </div>
    </>
  );
}

type Executar = (caminho: string, corpo: unknown) => void;

function Acoes({
  caso,
  ocupado,
  executar,
}: {
  caso: CasoDetalhado;
  ocupado: boolean;
  executar: Executar;
}) {
  const [resultado, definirResultado] = useState<ResultadoDaInvestigacao>('Legitima');

  if (caso.acoesPermitidas.length === 0) {
    return null;
  }

  return (
    <div className="acoes acoes--caso">
      {permite(caso, 'assumir') ? (
        <button
          type="button"
          className="botao botao--principal"
          disabled={ocupado}
          onClick={() => executar('assumir', { versao: caso.versao })}
        >
          Assumir o caso
        </button>
      ) : null}

      {permite(caso, 'resolver') ? (
        <form
          className="formulario-em-linha"
          onSubmit={(evento) => {
            evento.preventDefault();
            executar('resolucao', { resultado, versao: caso.versao });
          }}
        >
          <label className="campo">
            <span className="campo__rotulo">Resultado</span>
            <select
              value={resultado}
              onChange={(e) =>
                definirResultado(e.target.value as ResultadoDaInvestigacao)
              }
            >
              {RESULTADOS.map((opcao) => (
                <option key={opcao} value={opcao}>
                  {ROTULO_DO_RESULTADO[opcao]}
                </option>
              ))}
            </select>
          </label>

          <button type="submit" className="botao botao--principal" disabled={ocupado}>
            Resolver
          </button>
        </form>
      ) : null}
    </div>
  );
}

function AlertasDoCaso({ alertas }: { alertas: AlertaDoCaso[] }) {
  return (
    <table className="tabela">
      <thead>
        <tr>
          <th scope="col">Transação</th>
          <th scope="col">Cliente</th>
          <th scope="col">Valor</th>
          <th scope="col">Score</th>
          <th scope="col">Decisão</th>
          <th scope="col">Prioridade</th>
          <th scope="col">Sinais</th>
        </tr>
      </thead>
      <tbody>
        {alertas.map((alerta) => (
          <tr key={alerta.id}>
            <th scope="row">
              <Link to={`/transacoes/${alerta.transacaoId}`} className="ligacao">
                <code>{alerta.identificadorExterno ?? alerta.transacaoId}</code>
              </Link>
            </th>
            <td>{alerta.clienteExternoId ?? '—'}</td>
            <td className="numerico">
              {alerta.valor !== null && alerta.moeda
                ? alerta.valor.toLocaleString('pt-BR', {
                    style: 'currency',
                    currency: alerta.moeda,
                  })
                : '—'}
            </td>
            <td>
              <Score valor={alerta.score} />
            </td>
            <td>
              <SeloDeDecisao decisao={alerta.decisao} />
            </td>
            <td>
              <span
                className={`selo selo--prioridade-${alerta.prioridade.toLowerCase()}`}
              >
                {ROTULO_DA_PRIORIDADE[alerta.prioridade]}
              </span>
            </td>
            <td>
              <ul className="sinais-resumidos">
                {alerta.sinais.map((sinal) => (
                  <li key={sinal.tipo}>
                    {rotularTipoDeRegra(sinal.tipo)}{' '}
                    <span className="numerico">+{sinal.pontos}</span>
                  </li>
                ))}
              </ul>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function Notas({
  caso,
  ocupado,
  executar,
}: {
  caso: CasoDetalhado;
  ocupado: boolean;
  executar: Executar;
}) {
  const [conteudo, definirConteudo] = useState('');
  const podeEscrever = permite(caso, 'adicionarNota');

  return (
    <>
      {podeEscrever ? (
        <form
          className="nota__formulario"
          onSubmit={(evento) => {
            evento.preventDefault();
            executar('notas', { conteudo, versao: caso.versao });
            definirConteudo('');
          }}
        >
          <label className="campo">
            <span className="campo__rotulo">Nova nota</span>
            <textarea
              rows={3}
              value={conteudo}
              maxLength={4000}
              placeholder="O que você apurou até agora."
              onChange={(e) => definirConteudo(e.target.value)}
            />
          </label>

          <button
            type="submit"
            className="botao"
            disabled={ocupado || conteudo.trim().length < 3}
          >
            Anotar
          </button>

          <p className="campo__ajuda">
            Notas não são editáveis nem apagáveis. Uma correção vira uma nota nova.
          </p>
        </form>
      ) : null}

      {caso.notas.length === 0 ? (
        <p className="pagina__resumo">Nenhuma nota ainda.</p>
      ) : (
        <ul className="notas">
          {caso.notas.map((nota) => (
            <NotaDaInvestigacao key={nota.id} nota={nota} />
          ))}
        </ul>
      )}
    </>
  );
}

function NotaDaInvestigacao({ nota }: { nota: NotaDoCaso }) {
  return (
    <li className="nota">
      <div className="nota__cabecalho">
        <span className="nota__autor">{nota.autorNome}</span>
        <span className="nota__quando">
          {new Date(nota.criadaEm).toLocaleString('pt-BR')}
        </span>
      </div>
      {/*
        Texto, sempre. Nunca `dangerouslySetInnerHTML`: o React escapa por
        padrão, e é essa a defesa de verdade contra XSS. O backend recusa
        marcação na entrada, mas quem garante que nada executa aqui é a saída.
      */}
      <p className="nota__conteudo">{nota.conteudo}</p>
    </li>
  );
}

function Timeline({ eventos }: { eventos: EventoDoCaso[] }) {
  return (
    <ol className="timeline" aria-label="Histórico do caso">
      {eventos.map((evento) => (
        <li key={evento.id} className={`timeline__item timeline__item--${evento.tipo}`}>
          <p className="timeline__descricao">{evento.descricao}</p>
          <p className="timeline__origem">
            {evento.autorNome} · {new Date(evento.ocorridoEm).toLocaleString('pt-BR')}
          </p>
        </li>
      ))}
    </ol>
  );
}
