import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router';
import { requisitar } from '../api/clienteHttp';
import { rotularTipoDeRegra, type PerfilVigente } from '../api/risco';
import {
  podeAdministrarRegras,
  situacaoDaRegra,
  valoresIniciais,
  type RegraAdministrada,
  type TipoDeRegraDisponivel,
} from '../api/regras';
import { useSessao } from '../sessao/contextoDeSessao';
import { EstadoDeCarregamento, EstadoDeErro } from '../componentes/Estados';
import { CamposDaConfiguracao } from '../componentes/CamposDaConfiguracao';

/**
 * O catálogo de regras e os limiares que traduzem score em decisão.
 *
 * **Duas telas em uma, e de propósito.** Para o analista e o auditor é uma
 * página de leitura: o que está valendo e por quê — eles precisam disso para
 * entender o próprio score. Para a supervisão é a tela de administração.
 *
 * A separação não é cosmética: o backend responde `403` para quem não é
 * supervisão, e é ele quem decide. A tela apenas evita oferecer o que seria
 * recusado (CLAUDE.md seção 52).
 *
 * **Nada aqui muda o motor sozinho.** Criar uma regra escreve um rascunho;
 * publicar é outro passo, na tela da regra. Os números continuam sendo
 * configuração de demonstração deste projeto, e a página diz isso.
 */
export function PaginaDeRegras() {
  const sessao = useSessao();
  const administra = podeAdministrarRegras(sessao.usuario?.perfil);

  const perfil = useQuery({
    queryKey: ['regras', 'perfil'],
    queryFn: ({ signal }) =>
      requisitar<PerfilVigente>('/api/regras/perfil', { sinal: signal }),
  });

  const gestao = useQuery({
    queryKey: ['regras', 'gestao'],
    enabled: administra,
    queryFn: ({ signal }) =>
      requisitar<RegraAdministrada[]>('/api/regras/gestao', { sinal: signal }),
  });

  return (
    <section className="pagina pagina--larga">
      <h1>Regras</h1>
      <p className="pagina__resumo">
        Catálogo fechado e tipado de regras. Cada regra produz um sinal explicável e
        soma uma contribuição ao score.
      </p>

      {perfil.isPending ? <EstadoDeCarregamento rotulo="Carregando regras..." /> : null}

      {perfil.isError ? (
        <EstadoDeErro
          erro={perfil.error}
          aoTentarDeNovo={() => void perfil.refetch()}
        />
      ) : null}

      {perfil.isSuccess ? (
        <>
          <CartaoDoPerfil perfil={perfil.data} administra={administra} />

          {administra ? (
            <RegrasAdministradas consulta={gestao} />
          ) : (
            <TabelaDeVigentes perfil={perfil.data} />
          )}
        </>
      ) : null}
    </section>
  );
}

// ---------------------------------------------------------------------------
// Perfil de risco
// ---------------------------------------------------------------------------

function CartaoDoPerfil({
  perfil,
  administra,
}: {
  perfil: PerfilVigente;
  administra: boolean;
}) {
  const [editando, definirEditando] = useState(false);

  return (
    <div className="cartao">
      <h2>Perfil de risco — versão {perfil.numero}</h2>

      <ul className="faixas">
        <li className="faixa">
          <span className="selo selo--permitir">Permitir</span>
          <span className="faixa__intervalo">0 a {perfil.limiarDeRevisao - 1}</span>
        </li>
        <li className="faixa">
          <span className="selo selo--revisar">Revisar</span>
          <span className="faixa__intervalo">
            {perfil.limiarDeRevisao} a {perfil.limiarDeBloqueio - 1}
          </span>
        </li>
        <li className="faixa">
          <span className="selo selo--bloquear">Bloquear</span>
          <span className="faixa__intervalo">{perfil.limiarDeBloqueio} a 100</span>
        </li>
      </ul>

      <p className="pagina__resumo">
        Publicado em {new Date(perfil.publicadaEm).toLocaleString('pt-BR')}. Os limiares
        e os pesos são configuração de demonstração deste projeto — não são padrão de
        mercado nem recomendação oficial.
      </p>

      {administra && !editando ? (
        <button type="button" className="botao" onClick={() => definirEditando(true)}>
          Ajustar limiares
        </button>
      ) : null}

      {administra && editando ? (
        <FormularioDeLimiares perfil={perfil} aoFechar={() => definirEditando(false)} />
      ) : null}
    </div>
  );
}

function FormularioDeLimiares({
  perfil,
  aoFechar,
}: {
  perfil: PerfilVigente;
  aoFechar: () => void;
}) {
  const filaDeConsultas = useQueryClient();
  const [revisao, definirRevisao] = useState(perfil.limiarDeRevisao);
  const [bloqueio, definirBloqueio] = useState(perfil.limiarDeBloqueio);

  const publicar = useMutation({
    mutationFn: () =>
      requisitar<unknown>('/api/regras/perfil/limiares', {
        metodo: 'POST',
        corpo: {
          limiarDeRevisao: revisao,
          limiarDeBloqueio: bloqueio,
          // O número da versão vigente é o token de concorrência do perfil:
          // dois supervisores mexendo ao mesmo tempo não se sobrescrevem.
          numeroDaVersaoVigente: perfil.numero,
        },
      }),
    onSuccess: async () => {
      await filaDeConsultas.invalidateQueries({ queryKey: ['regras'] });
      aoFechar();
    },
  });

  const invalido = revisao >= bloqueio || revisao < 1 || bloqueio > 100;

  return (
    <form
      onSubmit={(evento) => {
        evento.preventDefault();
        publicar.mutate();
      }}
    >
      <div className="formulario-em-linha">
        <label className="campo">
          <span className="campo__rotulo">Limiar de revisão</span>
          <input
            type="number"
            value={revisao}
            min={1}
            max={100}
            onChange={(evento) => definirRevisao(Number(evento.target.value))}
          />
        </label>

        <label className="campo">
          <span className="campo__rotulo">Limiar de bloqueio</span>
          <input
            type="number"
            value={bloqueio}
            min={1}
            max={100}
            onChange={(evento) => definirBloqueio(Number(evento.target.value))}
          />
        </label>
      </div>

      {invalido ? (
        <p className="pagina__resumo" role="status">
          O limiar de revisão precisa ser menor que o de bloqueio — senão a faixa de
          revisão fica vazia e uma das três decisões nunca acontece.
        </p>
      ) : null}

      {publicar.isError ? <EstadoDeErro erro={publicar.error} /> : null}

      <div className="acoes acoes--caso">
        <button
          type="submit"
          className="botao botao--principal"
          disabled={invalido || publicar.isPending}
        >
          Publicar limiares
        </button>
        <button type="button" className="botao" onClick={aoFechar}>
          Cancelar
        </button>
      </div>

      <p className="pagina__resumo">
        Publicar cria uma versão nova do perfil. As avaliações já feitas continuam
        apontando para a versão que valia quando aconteceram.
      </p>
    </form>
  );
}

// ---------------------------------------------------------------------------
// Leitura para quem não administra
// ---------------------------------------------------------------------------

function TabelaDeVigentes({ perfil }: { perfil: PerfilVigente }) {
  return (
    <table className="tabela">
      <caption className="tabela__legenda">
        {perfil.regras.length} regra(s) em vigor
      </caption>
      <thead>
        <tr>
          <th scope="col">Regra</th>
          <th scope="col">Configuração</th>
          <th scope="col">Pontos</th>
          <th scope="col">Versão</th>
        </tr>
      </thead>
      <tbody>
        {perfil.regras.map((regra) => (
          <tr key={regra.id}>
            <th scope="row">{regra.nome}</th>
            <td>{regra.configuracao}</td>
            <td className="numerico">+{regra.pontos}</td>
            <td className="numerico">{regra.versaoAtual}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

// ---------------------------------------------------------------------------
// Administração
// ---------------------------------------------------------------------------

function RegrasAdministradas({
  consulta,
}: {
  consulta: ReturnType<typeof useQuery<RegraAdministrada[]>>;
}) {
  const [criando, definirCriando] = useState(false);

  if (consulta.isPending) {
    return <EstadoDeCarregamento rotulo="Carregando regras..." />;
  }

  if (consulta.isError) {
    return (
      <EstadoDeErro
        erro={consulta.error}
        aoTentarDeNovo={() => void consulta.refetch()}
      />
    );
  }

  const regras = consulta.data ?? [];

  return (
    <>
      <div className="acoes acoes--caso">
        <button
          type="button"
          className="botao botao--principal"
          onClick={() => definirCriando((atual) => !atual)}
        >
          {criando ? 'Fechar' : 'Nova regra'}
        </button>
      </div>

      {criando ? (
        <FormularioDeNovaRegra aoConcluir={() => definirCriando(false)} />
      ) : null}

      <table className="tabela">
        <caption className="tabela__legenda">
          {regras.length} regra(s) no catálogo
        </caption>
        <thead>
          <tr>
            <th scope="col">Regra</th>
            <th scope="col">Tipo</th>
            <th scope="col">Configuração em vigor</th>
            <th scope="col">Pontos</th>
            <th scope="col">Versão</th>
            <th scope="col">Situação</th>
          </tr>
        </thead>
        <tbody>
          {regras.map((regra) => (
            <tr key={regra.id}>
              <th scope="row">
                <Link to={`/regras/${regra.id}`} className="ligacao">
                  {regra.nome}
                </Link>
              </th>
              <td>{rotularTipoDeRegra(regra.tipo)}</td>
              <td>{regra.configuracaoVigente ?? '—'}</td>
              <td className="numerico">
                {regra.pontosVigentes === null ? '—' : `+${regra.pontosVigentes}`}
              </td>
              <td className="numerico">{regra.numeroDaVersaoVigente ?? '—'}</td>
              <td>{situacaoDaRegra(regra)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

function FormularioDeNovaRegra({ aoConcluir }: { aoConcluir: () => void }) {
  const filaDeConsultas = useQueryClient();

  const tipos = useQuery({
    queryKey: ['regras', 'tipos'],
    queryFn: ({ signal }) =>
      requisitar<TipoDeRegraDisponivel[]>('/api/regras/tipos', { sinal: signal }),
  });

  const [tipoEscolhido, definirTipoEscolhido] = useState<string>('');
  const [nome, definirNome] = useState('');
  const [pontos, definirPontos] = useState(10);
  const [valores, definirValores] = useState<Record<string, number>>({});

  const tipo = tipos.data?.find((t) => t.tipo === tipoEscolhido);

  const criar = useMutation({
    mutationFn: () =>
      requisitar<unknown>('/api/regras', {
        metodo: 'POST',
        corpo: { tipo: tipoEscolhido, nome, configuracao: valores, pontos },
      }),
    onSuccess: async () => {
      await filaDeConsultas.invalidateQueries({ queryKey: ['regras'] });
      aoConcluir();
    },
  });

  function escolherTipo(escolhido: string) {
    definirTipoEscolhido(escolhido);

    const descricao = tipos.data?.find((t) => t.tipo === escolhido);

    if (descricao) {
      definirValores(valoresIniciais(descricao));
      definirPontos(descricao.pontosSugeridos);
    }
  }

  if (tipos.isPending) {
    return <EstadoDeCarregamento rotulo="Carregando tipos de regra..." />;
  }

  if (tipos.isError) {
    return (
      <EstadoDeErro erro={tipos.error} aoTentarDeNovo={() => void tipos.refetch()} />
    );
  }

  return (
    <form
      className="cartao"
      onSubmit={(evento) => {
        evento.preventDefault();
        criar.mutate();
      }}
    >
      <h2>Nova regra</h2>

      <p className="pagina__resumo">
        O catálogo de tipos é fechado: configurar é escolher números dentro de um
        contrato conhecido, nunca escrever uma condição que o sistema executa. A regra
        nasce como rascunho e não vale para ninguém até ser publicada.
      </p>

      <label className="campo">
        <span className="campo__rotulo">Tipo</span>
        <select
          value={tipoEscolhido}
          onChange={(evento) => escolherTipo(evento.target.value)}
        >
          <option value="">Escolha um tipo</option>
          {tipos.data.map((disponivel) => (
            <option key={disponivel.tipo} value={disponivel.tipo}>
              {disponivel.rotulo}
            </option>
          ))}
        </select>
      </label>

      {tipo ? (
        <>
          <p className="pagina__resumo">{tipo.resumo}</p>

          <label className="campo">
            <span className="campo__rotulo">Nome</span>
            <input
              value={nome}
              maxLength={120}
              onChange={(evento) => definirNome(evento.target.value)}
            />
          </label>

          <CamposDaConfiguracao
            campos={tipo.campos}
            valores={valores}
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
        </>
      ) : null}

      {criar.isError ? <EstadoDeErro erro={criar.error} /> : null}

      <div className="acoes acoes--caso">
        <button
          type="submit"
          className="botao botao--principal"
          disabled={!tipo || nome.trim().length < 3 || criar.isPending}
        >
          Salvar rascunho
        </button>
        <button type="button" className="botao" onClick={aoConcluir}>
          Cancelar
        </button>
      </div>
    </form>
  );
}
