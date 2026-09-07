import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router';
import { requisitar } from '../api/clienteHttp';
import {
  ROTULO_DO_STATUS,
  estaEmAndamento,
  type BacktestDetalhado,
  type PaginaDeBacktests as Pagina,
} from '../api/backtests';
import { rotularTipoDeRegra, type PerfilVigente } from '../api/risco';
import type { RegraAdministrada } from '../api/regras';
import {
  EstadoDeCarregamento,
  EstadoDeErro,
  EstadoVazio,
} from '../componentes/Estados';

/**
 * Backtests: simular um perfil candidato sobre o histórico antes de publicar.
 *
 * **Nada aqui muda o motor.** Um backtest é um ensaio sobre uma decisão que
 * ainda não foi tomada; publicar continua sendo outro ato, na tela da regra.
 *
 * A lista repergunta sozinha enquanto houver execução em andamento — e para
 * de perguntar quando não houver. Um intervalo fixo faria a tela consultar o
 * servidor para sempre depois que a última execução terminasse.
 */
export function PaginaDeBacktests() {
  const [criando, definirCriando] = useState(false);

  const execucoes = useQuery({
    queryKey: ['backtests'],
    queryFn: ({ signal }) => requisitar<Pagina>('/api/backtests', { sinal: signal }),
    refetchInterval: (consulta) =>
      consulta.state.data?.itens.some((item) => estaEmAndamento(item.status))
        ? 2_000
        : false,
  });

  return (
    <section className="pagina pagina--larga">
      <h1>Backtests</h1>
      <p className="pagina__resumo">
        Aplica um perfil candidato sobre transações já recebidas e mostra o que mudaria
        — sem alterar nenhuma avaliação, alerta ou caso. A execução é assíncrona: o
        resultado aparece aqui quando terminar.
      </p>

      <div className="acoes acoes--caso">
        <button
          type="button"
          className="botao botao--principal"
          onClick={() => definirCriando((atual) => !atual)}
        >
          {criando ? 'Fechar' : 'Nova simulação'}
        </button>
      </div>

      {criando ? (
        <FormularioDeBacktest aoConcluir={() => definirCriando(false)} />
      ) : null}

      {execucoes.isPending ? (
        <EstadoDeCarregamento rotulo="Carregando execuções..." />
      ) : null}

      {execucoes.isError ? (
        <EstadoDeErro
          erro={execucoes.error}
          aoTentarDeNovo={() => void execucoes.refetch()}
        />
      ) : null}

      {execucoes.isSuccess ? <Tabela pagina={execucoes.data} /> : null}
    </section>
  );
}

function Tabela({ pagina }: { pagina: Pagina }) {
  if (pagina.itens.length === 0) {
    return (
      <EstadoVazio
        titulo="Nenhuma simulação ainda"
        descricao="Escreva um rascunho numa regra e simule o impacto dele antes de publicar."
      />
    );
  }

  return (
    <table className="tabela">
      <caption className="tabela__legenda">{pagina.totalDeItens} execução(ões)</caption>
      <thead>
        <tr>
          <th scope="col">Simulação</th>
          <th scope="col">Período</th>
          <th scope="col">Situação</th>
          <th scope="col">Analisadas</th>
          <th scope="col">Mudariam</th>
          <th scope="col">Pedido por</th>
        </tr>
      </thead>
      <tbody>
        {pagina.itens.map((execucao) => (
          <tr key={execucao.id}>
            <th scope="row">
              <Link to={`/backtests/${execucao.id}`} className="ligacao">
                {execucao.descricao}
              </Link>
            </th>
            <td>
              {new Date(execucao.inicio).toLocaleDateString('pt-BR')} a{' '}
              {new Date(execucao.fim).toLocaleDateString('pt-BR')}
            </td>
            <td>{ROTULO_DO_STATUS[execucao.status]}</td>
            <td className="numerico">{execucao.totalAnalisado ?? '—'}</td>
            <td className="numerico">{execucao.totalDeMudancas ?? '—'}</td>
            <td>{execucao.solicitadaPor}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/**
 * O pedido de simulação.
 *
 * Só regras com rascunho aparecem na lista: sem alteração pendente o
 * candidato seria idêntico ao vigente, e o backend recusa com `409`. Ofertar
 * o que seria recusado só faria a pessoa descobrir depois de clicar.
 */
function FormularioDeBacktest({ aoConcluir }: { aoConcluir: () => void }) {
  const filaDeConsultas = useQueryClient();

  const regras = useQuery({
    queryKey: ['regras', 'gestao'],
    queryFn: ({ signal }) =>
      requisitar<RegraAdministrada[]>('/api/regras/gestao', { sinal: signal }),
  });

  const perfil = useQuery({
    queryKey: ['regras', 'perfil'],
    queryFn: ({ signal }) =>
      requisitar<PerfilVigente>('/api/regras/perfil', { sinal: signal }),
  });

  const [regraId, definirRegraId] = useState('');
  const [dias, definirDias] = useState(30);
  const [revisao, definirRevisao] = useState<number | null>(null);
  const [bloqueio, definirBloqueio] = useState<number | null>(null);

  const solicitar = useMutation({
    mutationFn: () => {
      const fim = new Date();
      const inicio = new Date(fim.getTime() - dias * 24 * 60 * 60 * 1000);

      return requisitar<BacktestDetalhado>('/api/backtests', {
        metodo: 'POST',
        corpo: {
          regraId: regraId === '' ? null : regraId,
          limiarDeRevisao: revisao,
          limiarDeBloqueio: bloqueio,
          inicio: inicio.toISOString(),
          fim: fim.toISOString(),
        },
      });
    },
    onSuccess: async () => {
      await filaDeConsultas.invalidateQueries({ queryKey: ['backtests'] });
      aoConcluir();
    },
  });

  if (regras.isPending || perfil.isPending) {
    return <EstadoDeCarregamento rotulo="Carregando regras..." />;
  }

  if (regras.isError) {
    return (
      <EstadoDeErro erro={regras.error} aoTentarDeNovo={() => void regras.refetch()} />
    );
  }

  if (perfil.isError) {
    return (
      <EstadoDeErro erro={perfil.error} aoTentarDeNovo={() => void perfil.refetch()} />
    );
  }

  const comRascunho = (regras.data ?? []).filter(
    (regra) => regra.rascunho !== null && regra.ativa,
  );

  const limiarDeRevisao = revisao ?? perfil.data.limiarDeRevisao;
  const limiarDeBloqueio = bloqueio ?? perfil.data.limiarDeBloqueio;
  const limiaresInvalidos =
    limiarDeRevisao >= limiarDeBloqueio ||
    limiarDeRevisao < 1 ||
    limiarDeBloqueio > 100;

  return (
    <form
      className="cartao"
      onSubmit={(evento) => {
        evento.preventDefault();
        solicitar.mutate();
      }}
    >
      <h2>Nova simulação</h2>

      <p className="pagina__resumo">
        A simulação compara o candidato com o perfil em vigor hoje (versão{' '}
        {perfil.data.numero}), sobre as transações do período escolhido. Nada é
        publicado, e nenhuma avaliação existente muda.
      </p>

      <label className="campo">
        <span className="campo__rotulo">Rascunho a simular</span>
        <select
          value={regraId}
          onChange={(evento) => definirRegraId(evento.target.value)}
        >
          <option value="">Somente os limiares</option>
          {comRascunho.map((regra) => (
            <option key={regra.id} value={regra.id}>
              {regra.nome} ({rotularTipoDeRegra(regra.tipo)})
            </option>
          ))}
        </select>
      </label>

      {comRascunho.length === 0 ? (
        <p className="pagina__resumo">
          Nenhuma regra tem alterações pendentes. Escreva um rascunho na tela de regras
          para simular a mudança dele.
        </p>
      ) : null}

      <div className="formulario-em-linha">
        <label className="campo">
          <span className="campo__rotulo">Período (dias)</span>
          <input
            type="number"
            value={dias}
            min={1}
            max={90}
            onChange={(evento) => definirDias(Number(evento.target.value))}
          />
        </label>

        <label className="campo">
          <span className="campo__rotulo">Limiar de revisão</span>
          <input
            type="number"
            value={limiarDeRevisao}
            min={1}
            max={100}
            onChange={(evento) => definirRevisao(Number(evento.target.value))}
          />
        </label>

        <label className="campo">
          <span className="campo__rotulo">Limiar de bloqueio</span>
          <input
            type="number"
            value={limiarDeBloqueio}
            min={1}
            max={100}
            onChange={(evento) => definirBloqueio(Number(evento.target.value))}
          />
        </label>
      </div>

      {limiaresInvalidos ? (
        <p className="pagina__resumo" role="status">
          O limiar de revisão precisa ser menor que o de bloqueio — senão a faixa de
          revisão fica vazia.
        </p>
      ) : null}

      {solicitar.isError ? <EstadoDeErro erro={solicitar.error} /> : null}

      <div className="acoes acoes--caso">
        <button
          type="submit"
          className="botao botao--principal"
          disabled={limiaresInvalidos || dias < 1 || dias > 90 || solicitar.isPending}
        >
          Simular
        </button>
        <button type="button" className="botao" onClick={aoConcluir}>
          Cancelar
        </button>
      </div>
    </form>
  );
}
