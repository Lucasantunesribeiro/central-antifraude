import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { requisitar } from '../api/clienteHttp';
import { mensagemAmigavel } from '../api/erros';
import {
  EstadoDeCarregamento,
  EstadoDeErro,
  EstadoVazio,
} from '../componentes/Estados';

interface Integracao {
  id: string;
  nome: string;
  ativa: boolean;
  criadaEm: string;
}

interface Credencial {
  id: string;
  identificadorPublico: string;
  criadaEm: string;
  usadaPelaUltimaVezEm: string | null;
  revogadaEm: string | null;
  motivoDaRevogacao: string | null;
}

interface IntegracaoDetalhada extends Integracao {
  credenciais: Credencial[];
}

interface CredencialEmitida {
  id: string;
  identificadorPublico: string;
  chave: string;
  criadaEm: string;
  aviso: string;
}

interface ListaDeIntegracoes {
  itens: Integracao[];
  total: number;
}

const CHAVE_DA_LISTA = ['integracoes', 'lista'] as const;

/**
 * Gestão de integrações — exclusiva do Administrador.
 *
 * A tela existe para uma sequência concreta: criar a integração, entregar a
 * chave ao integrador, e depois rotacionar ou revogar quando for preciso.
 *
 * A chave aparece **uma única vez**, logo após ser emitida, e some assim que
 * a tela é fechada. Isso não é limitação da interface: só o hash existe no
 * servidor, e não há endpoint que a mostre de novo.
 */
export function PaginaDeIntegracoes() {
  const cliente = useQueryClient();
  const [selecionada, definirSelecionada] = useState<string | null>(null);
  const [chaveRecemEmitida, definirChaveRecemEmitida] =
    useState<CredencialEmitida | null>(null);
  const [nomeNovo, definirNomeNovo] = useState('');

  const lista = useQuery({
    queryKey: CHAVE_DA_LISTA,
    queryFn: ({ signal }) =>
      requisitar<ListaDeIntegracoes>('/api/integracoes?tamanho=100', { sinal: signal }),
  });

  const detalhe = useQuery({
    queryKey: ['integracoes', 'detalhe', selecionada],
    queryFn: ({ signal }) =>
      requisitar<IntegracaoDetalhada>(`/api/integracoes/${selecionada}`, {
        sinal: signal,
      }),
    enabled: selecionada !== null,
  });

  const criar = useMutation({
    mutationFn: (nome: string) =>
      requisitar<{ integracao: Integracao; credencial: CredencialEmitida }>(
        '/api/integracoes',
        {
          metodo: 'POST',
          corpo: { nome },
        },
      ),
    onSuccess: async (resultado) => {
      definirChaveRecemEmitida(resultado.credencial);
      definirNomeNovo('');
      await cliente.invalidateQueries({ queryKey: CHAVE_DA_LISTA });
    },
  });

  const rotacionar = useMutation({
    mutationFn: (id: string) =>
      requisitar<CredencialEmitida>(`/api/integracoes/${id}/credenciais`, {
        metodo: 'POST',
      }),
    onSuccess: async (credencial) => {
      definirChaveRecemEmitida(credencial);
      await cliente.invalidateQueries({ queryKey: ['integracoes'] });
    },
  });

  const revogar = useMutation({
    mutationFn: ({ id, credencialId }: { id: string; credencialId: string }) =>
      requisitar<void>(`/api/integracoes/${id}/credenciais/${credencialId}`, {
        metodo: 'DELETE',
      }),
    onSuccess: async () => {
      await cliente.invalidateQueries({ queryKey: ['integracoes'] });
    },
  });

  const alternarAtivacao = useMutation({
    mutationFn: ({ id, ativa }: { id: string; ativa: boolean }) =>
      requisitar<Integracao>(`/api/integracoes/${id}/ativacao`, {
        metodo: 'PUT',
        corpo: { ativa },
      }),
    onSuccess: async () => {
      await cliente.invalidateQueries({ queryKey: ['integracoes'] });
    },
  });

  const erroDeAcao =
    criar.error ?? rotacionar.error ?? revogar.error ?? alternarAtivacao.error ?? null;

  return (
    <section className="pagina">
      <h1>Integrações</h1>
      <p className="pagina__resumo">
        Sistemas autorizados a enviar transações para avaliação de risco.
      </p>

      {chaveRecemEmitida !== null ? (
        <ChaveEmitida
          credencial={chaveRecemEmitida}
          aoFechar={() => definirChaveRecemEmitida(null)}
        />
      ) : null}

      <section className="cartao" aria-labelledby="titulo-nova">
        <h2 id="titulo-nova">Nova integração</h2>
        <form
          className="formulario-em-linha"
          onSubmit={(evento) => {
            evento.preventDefault();
            criar.mutate(nomeNovo);
          }}
        >
          <label className="campo" htmlFor="nome-da-integracao">
            <span className="campo__rotulo">Nome</span>
            <input
              id="nome-da-integracao"
              value={nomeNovo}
              onChange={(e) => definirNomeNovo(e.target.value)}
              placeholder="Checkout web"
              disabled={criar.isPending}
              required
            />
          </label>
          <button
            className="botao botao--principal"
            type="submit"
            disabled={criar.isPending}
          >
            {criar.isPending ? 'Criando...' : 'Criar e emitir chave'}
          </button>
        </form>

        {erroDeAcao !== null ? (
          <p className="entrada__erro" role="alert">
            {mensagemAmigavel(erroDeAcao)}
          </p>
        ) : null}
      </section>

      {lista.isPending ? (
        <EstadoDeCarregamento rotulo="Carregando integrações..." />
      ) : null}

      {lista.isError ? (
        <EstadoDeErro erro={lista.error} aoTentarDeNovo={() => void lista.refetch()} />
      ) : null}

      {lista.isSuccess && lista.data.itens.length === 0 ? (
        <EstadoVazio
          titulo="Nenhuma integração ainda."
          descricao="Crie a primeira para começar a receber transações."
        />
      ) : null}

      {lista.isSuccess && lista.data.itens.length > 0 ? (
        <table className="tabela">
          <caption className="tabela__legenda">
            {lista.data.total} integração(ões)
          </caption>
          <thead>
            <tr>
              <th scope="col">Nome</th>
              <th scope="col">Situação</th>
              <th scope="col">Criada em</th>
              <th scope="col">Ações</th>
            </tr>
          </thead>
          <tbody>
            {lista.data.itens.map((integracao) => (
              <tr key={integracao.id}>
                <th scope="row">{integracao.nome}</th>
                <td>
                  <span
                    className={`selo ${integracao.ativa ? 'selo--ok' : 'selo--falha'}`}
                  >
                    {integracao.ativa ? 'Ativa' : 'Inativa'}
                  </span>
                </td>
                <td>{new Date(integracao.criadaEm).toLocaleString('pt-BR')}</td>
                <td className="acoes">
                  <button
                    type="button"
                    className="botao"
                    onClick={() =>
                      definirSelecionada(
                        selecionada === integracao.id ? null : integracao.id,
                      )
                    }
                  >
                    {selecionada === integracao.id ? 'Ocultar chaves' : 'Ver chaves'}
                  </button>
                  <button
                    type="button"
                    className="botao"
                    disabled={alternarAtivacao.isPending}
                    onClick={() =>
                      alternarAtivacao.mutate({
                        id: integracao.id,
                        ativa: !integracao.ativa,
                      })
                    }
                  >
                    {integracao.ativa ? 'Desativar' : 'Reativar'}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}

      {selecionada !== null && detalhe.isSuccess ? (
        <section className="cartao" aria-labelledby="titulo-credenciais">
          <h2 id="titulo-credenciais">Chaves de {detalhe.data.nome}</h2>

          <table className="tabela">
            <thead>
              <tr>
                <th scope="col">Identificador</th>
                <th scope="col">Criada em</th>
                <th scope="col">Último uso</th>
                <th scope="col">Situação</th>
                <th scope="col">Ação</th>
              </tr>
            </thead>
            <tbody>
              {detalhe.data.credenciais.map((credencial) => (
                <tr key={credencial.id}>
                  {/* O identificador público, e nunca o segredo: é assim que
                      o administrador reconhece qual chave está revogando. */}
                  <th scope="row">
                    <code>{credencial.identificadorPublico}</code>
                  </th>
                  <td>{new Date(credencial.criadaEm).toLocaleDateString('pt-BR')}</td>
                  <td>
                    {credencial.usadaPelaUltimaVezEm
                      ? new Date(credencial.usadaPelaUltimaVezEm).toLocaleString(
                          'pt-BR',
                        )
                      : 'nunca usada'}
                  </td>
                  <td>
                    <span
                      className={`selo ${credencial.revogadaEm ? 'selo--falha' : 'selo--ok'}`}
                    >
                      {credencial.revogadaEm
                        ? (credencial.motivoDaRevogacao ?? 'Revogada')
                        : 'Ativa'}
                    </span>
                  </td>
                  <td>
                    {credencial.revogadaEm === null ? (
                      <button
                        type="button"
                        className="botao"
                        disabled={revogar.isPending}
                        onClick={() =>
                          revogar.mutate({
                            id: detalhe.data.id,
                            credencialId: credencial.id,
                          })
                        }
                      >
                        Revogar
                      </button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          <button
            type="button"
            className="botao botao--principal"
            disabled={rotacionar.isPending}
            onClick={() => rotacionar.mutate(detalhe.data.id)}
          >
            Emitir nova chave
          </button>
          <p className="estado__descricao">
            A chave antiga continua valendo até ser revogada — é o que permite trocar
            sem interromper a ingestão do integrador.
          </p>
        </section>
      ) : null}
    </section>
  );
}

/**
 * Exibe a chave recém-emitida.
 *
 * Deliberadamente difícil de ignorar: é a única vez que este valor existe do
 * lado de fora do integrador.
 */
function ChaveEmitida({
  credencial,
  aoFechar,
}: {
  credencial: CredencialEmitida;
  aoFechar: () => void;
}) {
  return (
    <section
      className="cartao cartao--destaque"
      role="alert"
      aria-labelledby="titulo-chave"
    >
      <h2 id="titulo-chave">Chave emitida</h2>
      <p className="estado__descricao">{credencial.aviso}</p>
      <pre className="chave">{credencial.chave}</pre>
      <button type="button" className="botao" onClick={aoFechar}>
        Já guardei
      </button>
    </section>
  );
}
