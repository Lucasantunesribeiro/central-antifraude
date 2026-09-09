import { createContext, useContext } from 'react';
import type { UsuarioAutenticado } from '../api/tipos';

/**
 * Contrato da sessao e o hook que a le.
 *
 * Separado do componente de proposito: um arquivo que exporta componentes e
 * outras coisas juntas quebra o fast refresh do Vite, e o efeito pratico e
 * perder o estado da tela a cada salvamento durante o desenvolvimento.
 */
export interface Sessao {
  usuario: UsuarioAutenticado | null;
  /** Verdadeiro enquanto a restauracao inicial nao terminou. */
  carregando: boolean;
  autenticado: boolean;
  entrar: (email: string, senha: string) => Promise<void>;
  /**
   * Entra na conta de demonstracao sem senha.
   *
   * O backend so aceita quando o ambiente marca a demo como aberta; e o
   * caminho que deixa qualquer visitante conhecer o produto sem credencial.
   */
  entrarComoDemo: () => Promise<void>;
  sair: () => Promise<void>;
}

export const ContextoDeSessao = createContext<Sessao | null>(null);

export function useSessao(): Sessao {
  const contexto = useContext(ContextoDeSessao);

  if (contexto === null) {
    throw new Error('useSessao precisa estar dentro de <ProvedorDeSessao>.');
  }

  return contexto;
}
