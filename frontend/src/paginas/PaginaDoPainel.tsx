import { useRef, useState, type KeyboardEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router';
import { requisitar } from '../api/clienteHttp';
import {
  DECISOES,
  ROTULO_DO_STATUS_DE_CASO,
  pico,
  periodoComAtividade,
  proporcao,
  quantidadeDe,
  type MetricaDeRegra,
  type Painel,
} from '../api/operacao';
import { rotularTipoDeRegra } from '../api/risco';
import { SeloDeDecisao } from '../componentes/Risco';
import {
  EstadoDeCarregamento,
  EstadoDeErro,
  EstadoVazio,
} from '../componentes/Estados';

const PERIODOS = [1, 7, 30, 90] as const;

/**
 * A janela escolhida. Por `dias` é ancorada em "agora" (o caminho normal); por
 * `intervalo` é um período absoluto — usado pelo salto "ver o período com
 * atividade", que impede a demonstração de abrir vazia quando o cenário fica
 * mais velho que a janela padrão.
 */
type Selecao =
  | { modo: 'dias'; dias: number }
  | { modo: 'intervalo'; de: string; ate: string; rotulo: string };

function consultaDaSelecao(selecao: Selecao): string {
  return selecao.modo === 'dias'
    ? `dias=${selecao.dias}`
    : `de=${encodeURIComponent(selecao.de)}&ate=${encodeURIComponent(selecao.ate)}`;
}

const formatadorDeMes = new Intl.DateTimeFormat('pt-BR', {
  month: 'long',
  year: 'numeric',
});

/**
 * Painel operacional.
 *
 * **Nenhum número aqui é decorativo** (ROADMAP 10.4). Cada um responde a uma
 * pergunta que alguém faz no começo do expediente: o que chegou, o que o motor
 * recomendou, o que espera gente e o que está esperando há tempo demais.
 *
 * Dois números parecidos aparecem de propósito. *Recebidas* conta pela chegada;
 * *avaliadas* conta pela decisão. Uma transação atrasada chega hoje sobre um
 * fato de ontem, e igualar os dois esconderia justamente o comportamento que o
 * produto existe para tratar (CLAUDE.md seção 15).
 */
export function PaginaDoPainel() {
  const [selecao, definirSelecao] = useState<Selecao>({ modo: 'dias', dias: 7 });
  const consulta = consultaDaSelecao(selecao);

  const painel = useQuery({
    queryKey: ['painel', consulta],
    queryFn: ({ signal }) =>
      requisitar<Painel>(`/api/painel?${consulta}`, { sinal: signal }),
  });

  return (
    <section className="pagina pagina--larga">
      <h1>Painel operacional</h1>

      <div className="filtros" role="group" aria-label="Período do painel">
        {PERIODOS.map((periodo) => {
          const ativo = selecao.modo === 'dias' && selecao.dias === periodo;
          return (
            <button
              key={periodo}
              type="button"
              className={ativo ? 'botao botao--principal' : 'botao'}
              aria-pressed={ativo}
              onClick={() => definirSelecao({ modo: 'dias', dias: periodo })}
            >
              {periodo === 1 ? '24 horas' : `${periodo} dias`}
            </button>
          );
        })}
      </div>

      {selecao.modo === 'intervalo' ? (
        <p className="painel__periodo" role="status">
          Mostrando {selecao.rotulo}.{' '}
          <button
            type="button"
            className="ligacao"
            onClick={() => definirSelecao({ modo: 'dias', dias: 7 })}
          >
            Voltar ao período recente
          </button>
        </p>
      ) : null}

      {painel.isPending ? <EstadoDeCarregamento rotulo="Carregando painel..." /> : null}

      {painel.isError ? (
        <EstadoDeErro
          erro={painel.error}
          aoTentarDeNovo={() => void painel.refetch()}
        />
      ) : null}

      {painel.isSuccess ? (
        <>
          {/*
            Quando a janela padrao abre vazia mas ha atividade antes dela — o
            caso da demonstracao, cujo cenario envelhece —, um salto leva o
            visitante direto ao periodo com dados, em vez de deixa-lo diante de
            zeros sem explicacao.
          */}
          <SaltoDeAtividade
            painel={painel.data}
            selecao={selecao}
            aoSaltar={definirSelecao}
          />
          {/*
            O que abre a tela: quantos alertas esperam alguem e o botao para ir
            trabalha-los. Antes esta pagina tinha ONZE blocos e nenhuma acao
            principal — quem chegava aqui sabia o estado do mundo e nao sabia o
            que fazer com ele.
          */}
          <Chamada painel={painel.data} />
          <Volume painel={painel.data} />

          {/*
            O resto vai para abas. Nada sumiu: tendencia, regras e saude do
            sistema continuam ali, atras de um clique. A tela deixa de exigir
            que se leia tudo para achar uma coisa.
          */}
          <Abas
            abas={[
              {
                id: 'resumo',
                rotulo: 'Resumo',
                conteudo: (
                  <>
                    <Decisoes painel={painel.data} />
                    <FilaHumana painel={painel.data} />
                  </>
                ),
              },
              {
                id: 'tendencia',
                rotulo: 'Tendência',
                conteudo: <Tendencia painel={painel.data} />,
              },
              {
                id: 'regras',
                rotulo: 'Regras',
                conteudo: (
                  <>
                    <Sinais painel={painel.data} />
                    <MetricasDeRegra consulta={consulta} />
                  </>
                ),
              },
              {
                id: 'sistema',
                rotulo: 'Sistema',
                conteudo: <SaudeDaFila painel={painel.data} />,
              },
            ]}
          />
        </>
      ) : null}
    </section>
  );
}

/**
 * A unica coisa que a tela pede que voce faca.
 *
 * Um painel sem acao principal transforma quem chega em espectador: ele fica
 * sabendo o estado do mundo e nao sabe por onde comecar. O numero grande e a
 * pergunta ("quantos esperam alguem?"), e o botao ao lado e a resposta.
 *
 * Quando nao ha nada esperando, o botao continua ali — so muda de tom e de
 * texto. Esconde-lo faria a tela mudar de forma conforme o dia, e quem usa
 * todo dia aprende pela posicao das coisas.
 */
/**
 * O salto para o período com atividade.
 *
 * Só aparece quando a janela atual (ancorada em "agora") veio vazia mas existe
 * atividade antes dela — o caso da demonstração, cujo cenário fica mais velho
 * que a janela padrão a cada dia que passa. Em vez de deixar o visitante diante
 * de zeros, oferece um clique até onde há dados. O rótulo muda conforme o tenant
 * seja ou não a organização de demonstração.
 *
 * Degrada com elegância: se a resposta não trouxer os limites de atividade — um
 * backend mais antigo no ar durante a janela de deploy —, nada é renderizado.
 */
function SaltoDeAtividade({
  painel,
  selecao,
  aoSaltar,
}: {
  painel: Painel;
  selecao: Selecao;
  aoSaltar: (selecao: Selecao) => void;
}) {
  // Já estamos num intervalo absoluto, ou a janela tem dados: nada a oferecer.
  if (selecao.modo !== 'dias') return null;
  if (painel.transacoesRecebidas > 0 || painel.transacoesAvaliadas > 0) return null;
  if (!painel.primeiraAtividadeEm || !painel.ultimaAtividadeEm) return null;

  const alvo = periodoComAtividade(
    painel.primeiraAtividadeEm,
    painel.ultimaAtividadeEm,
  );
  const mes = formatadorDeMes.format(new Date(painel.ultimaAtividadeEm));
  const rotulo = painel.periodoDeDemonstracao
    ? `o período da demonstração (${mes})`
    : `a atividade anterior (${mes})`;
  const acao = painel.periodoDeDemonstracao
    ? 'Ver o período da demonstração'
    : 'Ver atividade anterior';

  return (
    <EstadoVazio
      titulo="Nada neste período"
      descricao={`Não houve atividade na janela atual, mas há ${rotulo}.`}
      acao={
        <button
          type="button"
          className="botao botao--principal"
          onClick={() =>
            aoSaltar({ modo: 'intervalo', de: alvo.de, ate: alvo.ate, rotulo })
          }
        >
          {acao} →
        </button>
      }
    />
  );
}

function Chamada({ painel }: { painel: Painel }) {
  const esperando = painel.alertasAbertos;

  return (
    <section className={esperando > 0 ? 'chamada chamada--urgente' : 'chamada'}>
      <div className="chamada__numero">
        <span className="chamada__valor numerico">{esperando}</span>
        <span className="chamada__rotulo">
          {esperando === 1 ? 'alerta esperando alguém' : 'alertas esperando alguém'}
        </span>
      </div>

      <Link className="botao botao--principal botao--grande" to="/alertas">
        Trabalhar a fila
        <span aria-hidden="true">→</span>
      </Link>
    </section>
  );
}

/**
 * Abas: o mesmo conteudo, atras de um clique.
 *
 * Nao e um componente de biblioteca de proposito. O padrao ARIA de abas pede
 * tres coisas — `tablist`, `tab` com `aria-selected` e `tabpanel` amarrado por
 * `aria-controls` — e todas as tres cabem aqui. Uma dependencia nova para isso
 * seria peso sem problema resolvido.
 */
function Abas({
  abas,
}: {
  abas: readonly { id: string; rotulo: string; conteudo: React.ReactNode }[];
}) {
  const [ativa, definirAtiva] = useState(abas[0]!.id);
  const referencias = useRef<Record<string, HTMLButtonElement | null>>({});

  // Teclado no tablist (padrao ARIA Tabs): setas movem entre abas, Home/End vao
  // aos extremos. Sem isso, so o mouse alcanca tendencia, regras e sistema.
  function aoTeclar(evento: KeyboardEvent<HTMLButtonElement>, indice: number) {
    const ultimo = abas.length - 1;
    let destino: number | null = null;

    if (evento.key === 'ArrowRight') destino = indice === ultimo ? 0 : indice + 1;
    else if (evento.key === 'ArrowLeft') destino = indice === 0 ? ultimo : indice - 1;
    else if (evento.key === 'Home') destino = 0;
    else if (evento.key === 'End') destino = ultimo;

    if (destino === null) return;

    evento.preventDefault();
    const id = abas[destino]!.id;
    definirAtiva(id);
    referencias.current[id]?.focus();
  }

  return (
    <div className="abas">
      <div className="abas__lista" role="tablist" aria-label="Seções do painel">
        {abas.map((aba, indice) => (
          <button
            key={aba.id}
            ref={(elemento) => {
              referencias.current[aba.id] = elemento;
            }}
            type="button"
            role="tab"
            id={`aba-${aba.id}`}
            aria-selected={aba.id === ativa}
            aria-controls={`painel-${aba.id}`}
            tabIndex={aba.id === ativa ? 0 : -1}
            className={aba.id === ativa ? 'abas__aba abas__aba--ativa' : 'abas__aba'}
            onClick={() => definirAtiva(aba.id)}
            onKeyDown={(evento) => aoTeclar(evento, indice)}
          >
            {aba.rotulo}
          </button>
        ))}
      </div>

      {abas.map((aba) =>
        aba.id === ativa ? (
          <div
            key={aba.id}
            role="tabpanel"
            id={`painel-${aba.id}`}
            aria-labelledby={`aba-${aba.id}`}
            className="abas__conteudo"
          >
            {aba.conteudo}
          </div>
        ) : null,
      )}
    </div>
  );
}

/**
 * Os quatro numeros que respondem "como estamos agora".
 *
 * Caixas separadas, e nao uma lista dentro de um painel: cada indicador tem
 * moldura propria porque sao QUATRO perguntas diferentes, e o olho precisa
 * poder pular direto para a que interessa. Numa lista corrida, os quatro se
 * leem como um paragrafo de numeros.
 *
 * O terceiro e destacado de proposito. Volume e contexto; alerta sem dono e o
 * unico dos quatro que pede uma acao de alguem hoje — e destaque so significa
 * alguma coisa quando um item o tem e os outros nao.
 */
function Volume({ painel }: { painel: Painel }) {
  const casosEmAnalise =
    painel.casos.find((c) => c.chave === 'EmAnalise')?.quantidade ?? 0;
  const totalDeCasos = painel.casos.reduce((soma, c) => soma + c.quantidade, 0);

  const semAvaliacao = painel.transacoesRecebidas - painel.transacoesAvaliadas;

  return (
    <div className="indicadores">
      <Indicador rotulo="Recebidas" valor={painel.transacoesRecebidas} />
      <Indicador
        rotulo="Avaliadas"
        valor={painel.transacoesAvaliadas}
        nota={semAvaliacao > 0 ? `${semAvaliacao} sem decisão` : undefined}
      />
      <Indicador
        rotulo="Investigações"
        valor={totalDeCasos}
        nota={casosEmAnalise > 0 ? `${casosEmAnalise} em análise` : undefined}
      />
    </div>
  );
}

function Indicador({
  rotulo,
  valor,
  nota,
}: {
  rotulo: string;
  valor: number;
  /** So aparece quando diz algo que o numero sozinho nao diz. */
  nota?: string;
}) {
  return (
    <div className="indicador">
      <span className="indicador__rotulo">{rotulo}</span>
      <span className="indicador__valor numerico">{valor}</span>
      {nota ? <span className="indicador__nota">{nota}</span> : null}
    </div>
  );
}

/**
 * A saude do caminho assincrono, numa linha so.
 *
 * Saiu do bloco de volume porque nao e volume: e o unico numero da tela que
 * fala do SISTEMA, e nao do movimento. Zero e o estado saudavel — e quando
 * deixa de ser zero, alertas destas transacoes ainda nao existem.
 */
function SaudeDaFila({ painel }: { painel: Painel }) {
  return (
    <p className="rodape-tecnico">
      <span>
        Outbox: <strong>{painel.eventosPendentes} pendente(s)</strong>
      </span>
      <span>
        {painel.eventosPendentes > 0
          ? 'alertas destas transações ainda não foram criados'
          : 'o caminho assíncrono está em dia'}
      </span>
    </p>
  );
}

function Decisoes({ painel }: { painel: Painel }) {
  const total = painel.transacoesAvaliadas;

  return (
    <div className="cartao">
      <h2>Decisões no período</h2>

      <table className="tabela">
        <thead>
          <tr>
            <th scope="col">Decisão</th>
            <th scope="col">Transações</th>
            <th scope="col">Proporção</th>
          </tr>
        </thead>
        <tbody>
          {DECISOES.map((decisao) => {
            const quantidade = quantidadeDe(painel.decisoes, decisao);

            return (
              <tr key={decisao}>
                <th scope="row">
                  <SeloDeDecisao decisao={decisao} />
                </th>
                <td className="numerico">{quantidade}</td>
                <td className="numerico">{proporcao(quantidade, total)}%</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/**
 * A série diária.
 *
 * Barras compostas em CSS puro, sem biblioteca de gráfico: são três números
 * por dia e um teto conhecido. Uma dependência a mais para desenhar isto não
 * resolveria problema nenhum do domínio.
 *
 * A série vem sem buracos — um dia sem transação vale zero e não some. Uma
 * linha do tempo que pula dias faz um fim de semana parado parecer um pico na
 * segunda.
 */
function Tendencia({ painel }: { painel: Painel }) {
  const maior = pico(painel.tendencia);

  return (
    <div className="cartao">
      <h2>Tendência</h2>

      <table className="tabela">
        <caption className="tabela__legenda">
          Decisões por dia, do mais antigo para o mais recente
        </caption>
        <thead>
          <tr>
            <th scope="col">Dia</th>
            <th scope="col">Permitir</th>
            <th scope="col">Revisar</th>
            <th scope="col">Bloquear</th>
            <th scope="col">Total</th>
            <th scope="col">
              <span className="visualmente-oculto">Proporção do dia</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {painel.tendencia.map((dia) => (
            <tr key={dia.dia}>
              <th scope="row">{dia.dia}</th>
              <td className="numerico">{dia.permitir}</td>
              <td className="numerico">{dia.revisar}</td>
              <td className="numerico">{dia.bloquear}</td>
              <td className="numerico">{dia.total}</td>
              <td>
                <span
                  className="score__barra"
                  role="img"
                  aria-label={`${dia.total} transação(ões) em ${dia.dia}`}
                >
                  <span
                    className="score__preenchimento"
                    style={{ width: `${maior === 0 ? 0 : (dia.total / maior) * 100}%` }}
                  />
                </span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function FilaHumana({ painel }: { painel: Painel }) {
  return (
    <div className="cartao">
      <h2>O que espera alguém</h2>

      <dl className="descricao">
        <div>
          <dt>Alertas abertos</dt>
          <dd className="numerico">
            <Link to="/alertas" className="ligacao">
              {painel.alertasAbertos}
            </Link>
          </dd>
        </div>
        {painel.casos.map((caso) => (
          <div key={caso.chave}>
            <dt>Casos: {ROTULO_DO_STATUS_DE_CASO[caso.chave] ?? caso.chave}</dt>
            <dd className="numerico">{caso.quantidade}</dd>
          </div>
        ))}
        <div>
          <dt>Casos parados</dt>
          <dd className="numerico">{painel.casosAntigos}</dd>
        </div>
      </dl>

      <p className="pagina__resumo">
        Um caso conta como parado quando está aberto há mais de{' '}
        {painel.diasParaCasoAntigo} dias sem resolução. O prazo é configuração de
        demonstração deste projeto, não padrão de mercado.
      </p>
    </div>
  );
}

function Sinais({ painel }: { painel: Painel }) {
  if (painel.sinaisMaisFrequentes.length === 0) {
    return null;
  }

  return (
    <div className="cartao">
      <h2>Sinais mais frequentes</h2>

      <table className="tabela">
        <thead>
          <tr>
            <th scope="col">Tipo de regra</th>
            <th scope="col">Acionamentos</th>
            <th scope="col">Transações</th>
          </tr>
        </thead>
        <tbody>
          {painel.sinaisMaisFrequentes.map((sinal) => (
            <tr key={sinal.tipo}>
              <th scope="row">{rotularTipoDeRegra(sinal.tipo)}</th>
              <td className="numerico">{sinal.acionamentos}</td>
              <td>
                <Link to={`/transacoes?tipoDeRegra=${sinal.tipo}`} className="ligacao">
                  Ver transações
                </Link>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * Acionamentos por regra, cruzados com o veredito humano.
 *
 * **São contagens, e nunca nota de qualidade da regra** (ROADMAP 10.5). As
 * transações com veredito não são amostra aleatória: elas viraram caso porque
 * o motor as marcou. A tela mostra os números brutos com o denominador à vista
 * e não calcula taxa nenhuma.
 */
function MetricasDeRegra({ consulta }: { consulta: string }) {
  const metricas = useQuery({
    queryKey: ['painel', 'regras', consulta],
    queryFn: ({ signal }) =>
      requisitar<MetricaDeRegra[]>(`/api/painel/regras?${consulta}`, {
        sinal: signal,
      }),
  });

  if (metricas.isPending) {
    return <EstadoDeCarregamento rotulo="Carregando métricas de regra..." />;
  }

  if (metricas.isError) {
    return (
      <EstadoDeErro
        erro={metricas.error}
        aoTentarDeNovo={() => void metricas.refetch()}
      />
    );
  }

  if (metricas.data.length === 0) {
    return null;
  }

  return (
    <div className="cartao">
      <h2>Regras no período</h2>

      <div className="tabela-rolavel">
        <table className="tabela">
          <thead>
            <tr>
              <th scope="col">Regra</th>
              <th scope="col">Acionamentos</th>
              <th scope="col">Fraude confirmada</th>
              <th scope="col">Legítima</th>
              <th scope="col">Inconclusiva</th>
              <th scope="col">Sem investigação</th>
            </tr>
          </thead>
          <tbody>
            {metricas.data.map((metrica) => (
              <tr key={metrica.regraId}>
                <th scope="row">
                  <Link to={`/regras/${metrica.regraId}`} className="ligacao">
                    {metrica.nome}
                  </Link>
                </th>
                <td className="numerico">{metrica.acionamentos}</td>
                <td className="numerico">{metrica.fraudeConfirmada}</td>
                <td className="numerico">{metrica.legitima}</td>
                <td className="numerico">{metrica.inconclusiva}</td>
                <td className="numerico">{metrica.semResultadoConhecido}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <p className="pagina__resumo">
        <strong>Acionamento</strong> é um sinal produzido por essa regra numa avaliação
        do período. As colunas seguintes são o resultado da investigação humana das
        transações correspondentes — contagens, e não taxa de acerto: as transações
        investigadas não são amostra aleatória, porque viraram caso justamente porque o
        motor as marcou. Uma regra com muitas fraudes confirmadas pode estar apontando
        bem, ou pode ser a que a equipe costuma investigar.
      </p>
    </div>
  );
}
