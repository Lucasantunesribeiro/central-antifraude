import { NavLink, Outlet, useNavigate } from 'react-router';
import { LimiteDeErro } from '../componentes/LimiteDeErro';
import { ROTULO_DO_PERFIL, type PerfilDeUsuario } from '../api/tipos';
import { useSessao } from '../sessao/contextoDeSessao';

/** Um item de navegacao e quais perfis o enxergam. */
type ItemDeNavegacao = {
  para: string;
  rotulo: string;
  perfis?: readonly PerfilDeUsuario[];
};

/**
 * A navegacao agrupada por FUNCAO, e nao por ordem de construcao.
 *
 * Dez telas numa fila horizontal viravam uma lista sem hierarquia: nada
 * separava "trabalhar a fila do dia" de "publicar uma versao de regra". Os
 * tres grupos abaixo sao a divisao real do produto, e sao tambem a ordem em
 * que um analista percorre o trabalho.
 */
const GRUPOS: readonly { titulo: string; itens: readonly ItemDeNavegacao[] }[] = [
  {
    titulo: 'Operação',
    itens: [
      { para: '/painel', rotulo: 'Painel' },
      { para: '/alertas', rotulo: 'Alertas' },
      { para: '/casos', rotulo: 'Casos' },
      { para: '/transacoes', rotulo: 'Transações' },
    ],
  },
  {
    titulo: 'Risco',
    itens: [
      {
        // A tela de Regras e ADMINISTRACAO: cria, edita e publica versao. Ela
        // chama /api/regras/gestao, que o backend so abre para a supervisao.
        // Sem esta restricao, o item aparecia para o Analista, e clicar nele
        // levava a uma tela que respondia 403 — um erro na cara de quem so
        // queria olhar. O menu agora espelha a autorizacao real.
        para: '/regras',
        rotulo: 'Regras',
        perfis: ['Administrador', 'SupervisorDeFraude'],
      },
      {
        para: '/backtests',
        rotulo: 'Backtests',
        perfis: ['Administrador', 'SupervisorDeFraude'],
      },
    ],
  },
  {
    titulo: 'Governança',
    itens: [
      { para: '/auditoria', rotulo: 'Auditoria', perfis: ['Administrador', 'Auditor'] },
      { para: '/integracoes', rotulo: 'Integrações', perfis: ['Administrador'] },
      { para: '/usuarios', rotulo: 'Usuários', perfis: ['Administrador'] },
    ],
  },
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

  const gruposVisiveis = sessao.autenticado
    ? GRUPOS.map((grupo) => ({
        ...grupo,
        itens: grupo.itens.filter(
          (item) => !item.perfis || item.perfis.includes(sessao.usuario!.perfil),
        ),
      })).filter((grupo) => grupo.itens.length > 0)
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
          <span className="app__marca-ambiente">Console operacional</span>
        </div>

        <nav className="app__navegacao" aria-label="Navegação principal">
          {gruposVisiveis.map((grupo) => (
            <div className="app__grupo" key={grupo.titulo}>
              {/*
                O titulo do grupo e apresentacao, e nao um destino: um leitor de
                tela ja anuncia "navegação principal" pela regiao, e repetir a
                estrutura como cabecalho so alongaria a travessia.
              */}
              <span className="app__grupo-titulo" aria-hidden="true">
                {grupo.titulo}
              </span>
              {grupo.itens.map((item) => (
                <NavLink key={item.para} to={item.para}>
                  {item.rotulo}
                </NavLink>
              ))}
            </div>
          ))}

          {!sessao.autenticado ? (
            <div className="app__grupo">
              <NavLink to="/" end>
                Início
              </NavLink>
            </div>
          ) : null}
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
