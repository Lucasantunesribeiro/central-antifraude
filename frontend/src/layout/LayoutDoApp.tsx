import { NavLink, Outlet } from 'react-router';
import { LimiteDeErro } from '../componentes/LimiteDeErro';
import { useSessao } from '../sessao/useSessao';

/**
 * Shell da aplicacao: cabecalho, navegacao e area de conteudo.
 *
 * A navegacao mostra somente o que existe. O ROADMAP (secao 1.6) prefere isso
 * a exibir itens desabilitados de fases futuras - um menu cheio de links
 * mortos confunde quem esta vendo o produto pela primeira vez.
 */
export function LayoutDoApp() {
  const sessao = useSessao();

  return (
    <div className="app">
      <header className="app__cabecalho">
        <div className="app__marca">
          <span className="app__marca-nome">Central Antifraude</span>
          <span className="app__marca-ambiente">fundacao tecnica</span>
        </div>

        <nav className="app__navegacao" aria-label="Navegacao principal">
          <NavLink to="/" end>
            Inicio
          </NavLink>
          {sessao.autenticado ? <NavLink to="/console">Console</NavLink> : null}
        </nav>

        <div className="app__sessao">
          {sessao.autenticado ? (
            <span>{sessao.nome}</span>
          ) : (
            <span className="app__sessao-anonima">Sem sessao</span>
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
