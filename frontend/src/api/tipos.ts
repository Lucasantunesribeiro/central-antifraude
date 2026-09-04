/** Perfis de acesso. Espelha o enum fechado do backend. */
export type PerfilDeUsuario =
  'Administrador' | 'SupervisorDeFraude' | 'AnalistaDeFraude' | 'Auditor';

export interface UsuarioAutenticado {
  id: string;
  email: string;
  nomeCompleto: string;
  perfil: PerfilDeUsuario;
  organizacaoId: string;
}

export interface RespostaDeSessao {
  accessToken: string;
  expiraEm: string;
  usuario: UsuarioAutenticado;
}

export const ROTULO_DO_PERFIL: Record<PerfilDeUsuario, string> = {
  Administrador: 'Administrador',
  SupervisorDeFraude: 'Supervisor de fraude',
  AnalistaDeFraude: 'Analista de fraude',
  Auditor: 'Auditor',
};
