import { NavLink, Outlet, useNavigate } from 'react-router';
import { LimiteDeErro } from '../componentes/LimiteDeErro';
import { ROTULO_DO_PERFIL, type PerfilDeUsuario } from '../api/tipos';
import { useSessao } from '../sessao/contextoDeSessao';

/** Itens de navegacao e quais perfis os enxergam. */
const NAVEGACAO: readonly {
  para: string;
  rotulo: string;
  perfis?: readonly PerfilDeUsuario[];
}[] = [
  { para: '/painel', rotulo: 'Painel' },
  { para: '/alertas', rotulo: 'Alertas' },
  { para: '/transacoes', rotulo: 'Transações' },
  { para: '/regras', rotulo: 'Regras' },
  { para: '/integracoes', rotulo: 'Integrações', perfis: ['Administrador'] },
  { para: '/usuarios', rotulo: 'Usuários', perfis: ['Administrador'] },
];

/**
 * Shell da aplicacao.
 *
 * A navegacao mostra somente o que o perfil alcanca — o ROADMAP secao 1.6
 * prefere isso a exibir itens desabilitados, que confundem quem ve o produto
 * pela primeira vez.
 *
 * Isso e apresentacao, nao autorizacao: quem recusa de fato e a API.
 */
export function LayoutDoApp() {
  const sessao = useSessao();
  const navegar = useNavigate();

  const itensVisiveis = sessao.autenticado
    ? NAVEGACAO.filter(
        (item) => !item.perfis || item.perfis.includes(sessao.usuario!.perfil),
      )
    : [];

  async function sair() {
    await sessao.sair();
    navegar('/entrar', { replace: true });
  }

  return (
    <div className="app">
      <header className="app__cabecalho">
        <div className="app__marca">
          <span className="app__marca-nome">Central Antifraude</span>
        </div>

        <nav className="app__navegacao" aria-label="Navegacao principal">
          <NavLink to="/" end>
            Inicio
          </NavLink>
          {itensVisiveis.map((item) => (
            <NavLink key={item.para} to={item.para}>
              {item.rotulo}
            </NavLink>
          ))}
        </nav>

        <div className="app__sessao">
          {sessao.autenticado ? (
            <>
              <span className="app__sessao-usuario">
                {sessao.usuario!.nomeCompleto}
                <span className="app__sessao-perfil">
                  {ROTULO_DO_PERFIL[sessao.usuario!.perfil]}
                </span>
              </span>
              <button type="button" className="botao" onClick={() => void sair()}>
                Sair
              </button>
            </>
          ) : (
            <NavLink to="/entrar" className="botao">
              Entrar
            </NavLink>
          )}
        </div>
      </header>

      <main className="app__conteudo">
        <LimiteDeErro>
          <Outlet />
        </LimiteDeErro>
      </main>
    </div>
  );
}
