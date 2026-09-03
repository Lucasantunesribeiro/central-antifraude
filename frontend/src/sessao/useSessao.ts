/**
 * Estado de sessao do usuario.
 *
 * Placeholder deliberado: autenticacao humana e a Fase 1 do ROADMAP. O que
 * existe aqui e apenas o formato do contrato, para que o shell e a protecao de
 * rotas ja tenham um lugar definido para consultar - e para que a Fase 1
 * troque a implementacao sem mexer em quem consome.
 *
 * Nao ha login, token nem armazenamento nesta fase.
 */

export type PerfilDoUsuario =
  'Administrador' | 'SupervisorDeFraude' | 'AnalistaDeFraude' | 'Auditor';

export interface Sessao {
  autenticado: boolean;
  carregando: boolean;
  nome?: string;
  perfil?: PerfilDoUsuario;
}

export function useSessao(): Sessao {
  return { autenticado: false, carregando: false };
}
