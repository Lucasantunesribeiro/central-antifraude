import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router';
import { requisitar } from '../api/clienteHttp';
import {
  DECISOES,
  ROTULO_DO_STATUS_DE_CASO,
  pico,
  proporcao,
  quantidadeDe,
  type MetricaDeRegra,
  type Painel,
} from '../api/operacao';
import { rotularTipoDeRegra } from '../api/risco';
import { SeloDeDecisao } from '../componentes/Risco';
import { EstadoDeCarregamento, EstadoDeErro } from '../componentes/Estados';

const PERIODOS = [1, 7, 30, 90] as const;

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
  const [dias, definirDias] = useState<number>(7);

  const painel = useQuery({
    queryKey: ['painel', dias],
    queryFn: ({ signal }) =>
      requisitar<Painel>(`/api/painel?dias=${dias}`, { sinal: signal }),
  });

  return (
    <section className="pagina pagina--larga">
      <h1>Painel operacional</h1>
      <p className="pagina__resumo">
        O que aconteceu nesta organização no período, e o que ainda espera alguém.
      </p>

      <div className="filtros" role="group" aria-label="Período do painel">
        {PERIODOS.map((periodo) => (
          <button
            key={periodo}
            type="button"
            className={periodo === dias ? 'botao botao--principal' : 'botao'}
            aria-pressed={periodo === dias}
            onClick={() => definirDias(periodo)}
          >
            {periodo === 1 ? '24 horas' : `${periodo} dias`}
          </button>
        ))}
      </div>

      {painel.isPending ? <EstadoDeCarregamento rotulo="Carregando painel..." /> : null}

      {painel.isError ? (
        <EstadoDeErro
          erro={painel.error}
          aoTentarDeNovo={() => void painel.refetch()}
        />
      ) : null}

      {painel.isSuccess ? (
        <>
          <Volume painel={painel.data} />
          <Decisoes painel={painel.data} />
          <Tendencia painel={painel.data} />
          <FilaHumana painel={painel.data} />
          <Sinais painel={painel.data} />
          <MetricasDeRegra dias={dias} />
        </>
      ) : null}
    </section>
  );
}

function Volume({ painel }: { painel: Painel }) {
  return (
    <div className="cartao">
      <h2>Volume</h2>

      <dl className="descricao">
        <div>
          <dt>Transações recebidas</dt>
          <dd className="numerico">{painel.transacoesRecebidas}</dd>
        </div>
        <div>
          <dt>Transações avaliadas</dt>
          <dd className="numerico">{painel.transacoesAvaliadas}</dd>
        </div>
        <div>
          <dt>Eventos pendentes na fila</dt>
          <dd className="numerico">{painel.eventosPendentes}</dd>
        </div>
      </dl>

      <p className="pagina__resumo">
        <strong>Recebidas</strong> conta pela chegada; <strong>avaliadas</strong>, pela
        decisão. Uma transação atrasada chega hoje sobre um fato de ontem, então os dois
        números são diferentes por natureza.{' '}
        {painel.eventosPendentes > 0
          ? 'Há eventos esperando publicação: alertas destas transações ainda não foram criados.'
          : 'Nenhum evento esperando publicação — o caminho assíncrono está em dia.'}
      </p>
    </div>
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

      <p className="pagina__resumo">
        Recomendações de risco, não resultado financeiro: a Central Antifraude não
        autoriza, não captura e não liquida.
      </p>
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
function MetricasDeRegra({ dias }: { dias: number }) {
  const consulta = useQuery({
    queryKey: ['painel', 'regras', dias],
    queryFn: ({ signal }) =>
      requisitar<MetricaDeRegra[]>(`/api/painel/regras?dias=${dias}`, {
        sinal: signal,
      }),
  });

  if (consulta.isPending) {
    return <EstadoDeCarregamento rotulo="Carregando métricas de regra..." />;
  }

  if (consulta.isError) {
    return (
      <EstadoDeErro
        erro={consulta.error}
        aoTentarDeNovo={() => void consulta.refetch()}
      />
    );
  }

  if (consulta.data.length === 0) {
    return null;
  }

  return (
    <div className="cartao">
      <h2>Regras no período</h2>

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
          {consulta.data.map((metrica) => (
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
