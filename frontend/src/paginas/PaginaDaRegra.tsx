import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router';
import { requisitar } from '../api/clienteHttp';
import { rotularTipoDeRegra } from '../api/risco';
import {
  situacaoDaRegra,
  valoresIniciais,
  type RegraAdministrada,
  type TipoDeRegraDisponivel,
} from '../api/regras';
import { EstadoDeCarregamento, EstadoDeErro } from '../componentes/Estados';
import { CamposDaConfiguracao } from '../componentes/CamposDaConfiguracao';

/**
 * A regra por inteiro: rascunho, publicação e histórico de versões.
 *
 * **Três estados que a tela precisa distinguir**, porque confundi-los faria o
 * Supervisor achar que mudou o motor quando só escreveu um rascunho:
 *
 * - o que está **em vigor** — a versão que o perfil publicado carrega;
 * - o que está **escrito** — o rascunho, que não vale para ninguém;
 * - o que **já valeu** — as versões anteriores, que continuam explicando as
 *   avaliações que as usaram e nunca são alteradas.
 *
 * **Toda ação envia a versão que a tela leu.** É o que impede dois
 * supervisores de publicarem um por cima do outro; quem age com informação
 * velha recebe um conflito, e não um sucesso silencioso.
 */
export function PaginaDaRegra() {
  const { id = '' } = useParams();
  const filaDeConsultas = useQueryClient();
  const [erroDaAcao, definirErroDaAcao] = useState<unknown>(null);

  const consulta = useQuery({
    queryKey: ['regras', 'regra', id],
    queryFn: ({ signal }) =>
      requisitar<RegraAdministrada>(`/api/regras/${id}`, { sinal: signal }),
  });

  const acao = useMutation({
    mutationFn: ({
      caminho,
      metodo,
      corpo,
    }: {
      caminho: string;
      metodo: 'POST' | 'DELETE';
      corpo?: unknown;
    }) => requisitar<unknown>(`/api/regras/${id}${caminho}`, { metodo, corpo }),
    onSuccess: async () => {
      definirErroDaAcao(null);
      await filaDeConsultas.invalidateQueries({ queryKey: ['regras'] });
    },
    // O erro fica na tela em vez de sumir: um conflito de versão precisa ser
    // lido, e não engolido.
    onError: (erro) => definirErroDaAcao(erro),
  });

  if (consulta.isPending) {
    return <EstadoDeCarregamento rotulo="Carregando a regra..." />;
  }

  if (consulta.isError) {
    return (
      <EstadoDeErro
        erro={consulta.error}
        aoTentarDeNovo={() => void consulta.refetch()}
      />
    );
  }

  const regra = consulta.data;

  return (
    <section className="pagina pagina--larga">
      <p className="pagina__migalha">
        <Link to="/regras" className="ligacao">
          Regras
        </Link>
      </p>

      <header>
        <h1>{regra.nome}</h1>
        <p className="pagina__resumo">
          {rotularTipoDeRegra(regra.tipo)} · {situacaoDaRegra(regra)}
        </p>
      </header>

      {erroDaAcao ? <EstadoDeErro erro={erroDaAcao} /> : null}

      <div className="cartao">
        <h2>Em vigor</h2>

        {regra.numeroDaVersaoVigente === null ? (
          <p className="pagina__resumo">
            Esta regra nunca foi publicada. Ela existe no catálogo e o motor ainda não a
            executa.
          </p>
        ) : (
          <p className="pagina__resumo">
            Versão {regra.numeroDaVersaoVigente} · +{regra.pontosVigentes} ponto(s) ·{' '}
            {regra.configuracaoVigente}
            {regra.noPerfilVigente
              ? ''
              : ' — publicada, mas fora do perfil em vigor porque a regra está desativada.'}
          </p>
        )}

        <div className="acoes acoes--caso">
          <button
            type="button"
            className="botao"
            disabled={acao.isPending}
            onClick={() =>
              acao.mutate({
                caminho: '/ativacao',
                metodo: 'POST',
                corpo: { ativa: !regra.ativa, versao: regra.versao },
              })
            }
          >
            {regra.ativa ? 'Desativar regra' : 'Reativar regra'}
          </button>
        </div>

        <p className="pagina__resumo">
          Desativar não apaga nada: as versões publicadas continuam existindo e as
          avaliações que as usaram continuam explicáveis. A regra apenas sai das
          próximas versões do perfil.
        </p>
      </div>

      <EditorDeRascunho regra={regra} acao={acao} />

      <Historico regra={regra} />
    </section>
  );
}

type AcaoDaRegra = ReturnType<
  typeof useMutation<
    unknown,
    Error,
    { caminho: string; metodo: 'POST' | 'DELETE'; corpo?: unknown }
  >
>;

function EditorDeRascunho({
  regra,
  acao,
}: {
  regra: RegraAdministrada;
  acao: AcaoDaRegra;
}) {
  const filaDeConsultas = useQueryClient();

  const tipos = useQuery({
    queryKey: ['regras', 'tipos'],
    queryFn: ({ signal }) =>
      requisitar<TipoDeRegraDisponivel[]>('/api/regras/tipos', { sinal: signal }),
  });

  const descricao = tipos.data?.find((t) => t.tipo === regra.tipo);

  const base = regra.rascunho?.valores ?? regra.versoes[0]?.valores;
  const pontosBase = regra.rascunho?.pontos ?? regra.pontosVigentes ?? 10;

  const [nome, definirNome] = useState(regra.nome);
  const [pontos, definirPontos] = useState(pontosBase);
  const [valores, definirValores] = useState<Record<string, number>>(
    descricao ? valoresIniciais(descricao, base) : {},
  );
  const [erro, definirErro] = useState<unknown>(null);

  // Os valores locais só existem depois que o contrato do tipo chegou: na
  // primeira renderização a consulta ainda está em voo. Enviar o estado cru
  // faria "salvar sem tocar em nada" mandar uma configuração vazia — e o
  // backend recusaria por campo faltando, sem que a pessoa entendesse por quê.
  const preenchidos =
    Object.keys(valores).length > 0 || !descricao
      ? valores
      : valoresIniciais(descricao, base);

  const salvar = useMutation({
    mutationFn: () =>
      requisitar<unknown>(`/api/regras/${regra.id}/rascunho`, {
        metodo: 'PUT',
        corpo: { nome, configuracao: preenchidos, pontos, versao: regra.versao },
      }),
    onSuccess: async () => {
      definirErro(null);
      await filaDeConsultas.invalidateQueries({ queryKey: ['regras'] });
    },
    onError: (causa) => definirErro(causa),
  });

  if (tipos.isPending) {
    return <EstadoDeCarregamento rotulo="Carregando o contrato do tipo..." />;
  }

  if (tipos.isError || !descricao) {
    return (
      <EstadoDeErro erro={tipos.error} aoTentarDeNovo={() => void tipos.refetch()} />
    );
  }

  return (
    <form
      className="cartao"
      onSubmit={(evento) => {
        evento.preventDefault();
        salvar.mutate();
      }}
    >
      <h2>Rascunho</h2>

      <p className="pagina__resumo">
        {regra.rascunho
          ? `Há alterações escritas e não publicadas: ${regra.rascunho.configuracao}, +${regra.rascunho.pontos} ponto(s).`
          : 'Não há alterações pendentes. Editar abaixo cria um rascunho — nada muda no motor até publicar.'}
      </p>

      <label className="campo">
        <span className="campo__rotulo">Nome</span>
        <input
          value={nome}
          maxLength={120}
          onChange={(evento) => definirNome(evento.target.value)}
        />
      </label>

      <CamposDaConfiguracao
        campos={descricao.campos}
        valores={preenchidos}
        aoMudar={definirValores}
      />

      <label className="campo">
        <span className="campo__rotulo">Pontos</span>
        <input
          type="number"
          value={pontos}
          min={1}
          max={100}
          onChange={(evento) => definirPontos(Number(evento.target.value))}
        />
      </label>

      {erro ? <EstadoDeErro erro={erro} /> : null}

      <div className="acoes acoes--caso">
        <button
          type="submit"
          className="botao"
          disabled={salvar.isPending || nome.trim().length < 3}
        >
          Salvar rascunho
        </button>

        <button
          type="button"
          className="botao botao--principal"
          disabled={!regra.rascunho || acao.isPending}
          onClick={() =>
            acao.mutate({
              caminho: '/publicacao',
              metodo: 'POST',
              corpo: { versao: regra.versao },
            })
          }
        >
          Publicar versão
        </button>

        <button
          type="button"
          className="botao"
          disabled={!regra.rascunho || acao.isPending}
          onClick={() =>
            acao.mutate({
              caminho: `/rascunho?versao=${regra.versao}`,
              metodo: 'DELETE',
            })
          }
        >
          Descartar rascunho
        </button>
      </div>

      <p className="pagina__resumo">
        Publicar congela o rascunho em uma versão que nunca muda e cria uma versão nova
        do perfil de risco. As avaliações já feitas continuam apontando para a versão
        que valia quando aconteceram.
      </p>
    </form>
  );
}

function Historico({ regra }: { regra: RegraAdministrada }) {
  return (
    <>
      <h2>Histórico de versões</h2>

      <table className="tabela">
        <caption className="tabela__legenda">
          {regra.versoes.length} versão(ões) publicada(s)
        </caption>
        <thead>
          <tr>
            <th scope="col">Versão</th>
            <th scope="col">Configuração</th>
            <th scope="col">Pontos</th>
            <th scope="col">Publicada em</th>
          </tr>
        </thead>
        <tbody>
          {regra.versoes.map((versao) => (
            <tr key={versao.id}>
              <th scope="row" className="numerico">
                {versao.numero}
              </th>
              <td>{versao.configuracao}</td>
              <td className="numerico">+{versao.pontos}</td>
              <td>{new Date(versao.publicadaEm).toLocaleString('pt-BR')}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <p className="pagina__resumo">
        Uma versão publicada não é editada nem apagada. É ela que responde “por que esta
        decisão foi tomada naquele momento” para toda avaliação que a usou.
      </p>
    </>
  );
}
