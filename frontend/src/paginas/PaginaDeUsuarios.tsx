import { useQuery } from '@tanstack/react-query';
import { requisitar } from '../api/clienteHttp';
import { ROTULO_DO_PERFIL, type PerfilDeUsuario } from '../api/tipos';
import {
  EstadoDeCarregamento,
  EstadoDeErro,
  EstadoVazio,
} from '../componentes/Estados';

interface UsuarioResumido {
  id: string;
  email: string;
  nomeCompleto: string;
  perfil: PerfilDeUsuario;
  ativo: boolean;
  criadoEm: string;
}

interface RespostaPaginada {
  itens: UsuarioResumido[];
  pagina: number;
  tamanho: number;
  total: number;
  totalDePaginas: number;
}

/**
 * Listagem administrativa de usuarios.
 *
 * Exclusiva do Administrador — e a rota so aparece no menu para ele. Mas quem
 * de fato recusa e o backend: um Analista que digite /usuarios na barra de
 * endereco recebe 403 da API, nao uma tela vazia.
 */
export function PaginaDeUsuarios() {
  const consulta = useQuery({
    queryKey: ['usuarios', 'lista'],
    queryFn: ({ signal }) =>
      requisitar<RespostaPaginada>('/api/usuarios?tamanho=100', { sinal: signal }),
  });

  return (
    <section className="pagina">
      <h1>Usuarios</h1>
      <p className="pagina__resumo">Pessoas com acesso a esta organizacao.</p>

      {consulta.isPending ? (
        <EstadoDeCarregamento rotulo="Carregando usuarios..." />
      ) : null}

      {consulta.isError ? (
        <EstadoDeErro
          erro={consulta.error}
          aoTentarDeNovo={() => void consulta.refetch()}
        />
      ) : null}

      {consulta.isSuccess && consulta.data.itens.length === 0 ? (
        <EstadoVazio titulo="Nenhum usuario encontrado." />
      ) : null}

      {consulta.isSuccess && consulta.data.itens.length > 0 ? (
        <table className="tabela">
          <caption className="tabela__legenda">
            {consulta.data.total} usuario(s) nesta organizacao
          </caption>
          <thead>
            <tr>
              <th scope="col">Nome</th>
              <th scope="col">E-mail</th>
              <th scope="col">Perfil</th>
              <th scope="col">Situacao</th>
            </tr>
          </thead>
          <tbody>
            {consulta.data.itens.map((usuario) => (
              <tr key={usuario.id}>
                <th scope="row">{usuario.nomeCompleto}</th>
                <td>{usuario.email}</td>
                <td>{ROTULO_DO_PERFIL[usuario.perfil]}</td>
                <td>
                  {/* O texto carrega a informacao, nao so a cor. */}
                  <span
                    className={`selo ${usuario.ativo ? 'selo--ok' : 'selo--falha'}`}
                  >
                    {usuario.ativo ? 'Ativo' : 'Inativo'}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
    </section>
  );
}
