import { ROTULO_DO_PERFIL } from '../api/tipos';
import { useSessao } from '../sessao/contextoDeSessao';

/**
 * Area autenticada da Fase 1.
 *
 * Nao e o Painel Operacional do produto — esse chega na Fase 10, quando
 * existirem transacoes, alertas e casos para resumir. O que existe aqui e a
 * confirmacao de que a sessao esta de pe e de qual perfil ela carrega.
 */
export function PaginaDoPainel() {
  const { usuario } = useSessao();

  if (!usuario) {
    return null;
  }

  return (
    <section className="pagina">
      <h1>Sessao ativa</h1>
      <p className="pagina__resumo">
        As telas operacionais chegam junto das fases de dominio correspondentes.
      </p>

      <section className="cartao" aria-labelledby="titulo-sessao">
        <h2 id="titulo-sessao">Identidade</h2>
        <table className="tabela">
          <tbody>
            <tr>
              <th scope="row">Nome</th>
              <td>{usuario.nomeCompleto}</td>
            </tr>
            <tr>
              <th scope="row">E-mail</th>
              <td>{usuario.email}</td>
            </tr>
            <tr>
              <th scope="row">Perfil</th>
              <td>{ROTULO_DO_PERFIL[usuario.perfil]}</td>
            </tr>
            <tr>
              <th scope="row">Organizacao</th>
              <td>
                <code>{usuario.organizacaoId}</code>
              </td>
            </tr>
          </tbody>
        </table>
      </section>
    </section>
  );
}
