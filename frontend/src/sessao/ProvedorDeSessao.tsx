import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { requisitar, definirTokenDeAcesso } from '../api/clienteHttp';
import { ContextoDeSessao, type Sessao } from './contextoDeSessao';
import { ErroDaApi } from '../api/erros';
import type { RespostaDeSessao, UsuarioAutenticado } from '../api/tipos';

/**
 * Estado de sessao do usuario humano.
 *
 * Duas decisoes moram aqui, e as duas vem do ROADMAP secao 1.3:
 *
 * 1. **O access token fica so em memoria.** Nunca em localStorage nem em
 *    sessionStorage. Um XSS na aplicacao leria qualquer um dos dois; uma
 *    variavel de modulo desaparece quando a aba fecha.
 *
 * 2. **A continuidade vem do cookie HttpOnly.** Ao carregar a pagina, o
 *    frontend nao tem token nenhum — ele pergunta ao servidor
 *    (`POST /auth/refresh`) se o cookie ainda vale. E por isso que um F5 nao
 *    desloga: o navegador guarda o cookie, e o JavaScript nunca precisa
 *    ve-lo.
 */

/**
 * Renova o token um pouco antes de ele expirar.
 *
 * A margem existe para cobrir latencia de rede e diferenca de relogio entre
 * navegador e servidor. Sem ela, a renovacao chegaria com o token ja vencido
 * e o usuario cairia no meio de uma investigacao.
 */
const MARGEM_DE_RENOVACAO_EM_MS = 60_000;

export function ProvedorDeSessao({ children }: { children: ReactNode }) {
  const [usuario, definirUsuario] = useState<UsuarioAutenticado | null>(null);
  const [carregando, definirCarregando] = useState(true);
  const temporizador = useRef<ReturnType<typeof setTimeout> | null>(null);

  const limparRenovacao = useCallback(() => {
    if (temporizador.current !== null) {
      clearTimeout(temporizador.current);
      temporizador.current = null;
    }
  }, []);

  const encerrarLocalmente = useCallback(() => {
    limparRenovacao();
    definirTokenDeAcesso(null);
    definirUsuario(null);
  }, [limparRenovacao]);

  const aplicarSessao = useCallback(
    (sessao: RespostaDeSessao, agendar: () => void) => {
      definirTokenDeAcesso(sessao.accessToken);
      definirUsuario(sessao.usuario);

      const restanteEmMs =
        new Date(sessao.expiraEm).getTime() - Date.now() - MARGEM_DE_RENOVACAO_EM_MS;

      limparRenovacao();
      temporizador.current = setTimeout(agendar, Math.max(restanteEmMs, 5_000));
    },
    [limparRenovacao],
  );

  const renovar = useCallback(async (): Promise<boolean> => {
    try {
      const sessao = await requisitar<RespostaDeSessao>('/api/auth/refresh', {
        metodo: 'POST',
        semAutenticacao: true,
      });

      aplicarSessao(sessao, () => void renovar());
      return true;
    } catch (erro) {
      // 401 aqui e o caso normal de "nao ha sessao" ou "a sessao acabou".
      // Qualquer outro erro tambem termina em sessao encerrada: continuar
      // tentando renovar com uma credencial que o servidor recusou so
      // geraria ruido.
      if (!(erro instanceof ErroDaApi)) {
        console.error('Falha inesperada ao renovar a sessao', erro);
      }

      encerrarLocalmente();
      return false;
    }
  }, [aplicarSessao, encerrarLocalmente]);

  // Restauracao inicial: acontece uma vez, ao montar a aplicacao.
  useEffect(() => {
    let ativo = true;

    void renovar().finally(() => {
      if (ativo) {
        definirCarregando(false);
      }
    });

    return () => {
      ativo = false;
      limparRenovacao();
    };
    // Intencionalmente sem dependencias: restaurar sessao e um efeito de
    // montagem, e repeti-lo a cada render criaria um laco de renovacao.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const entrar = useCallback(
    async (email: string, senha: string) => {
      const sessao = await requisitar<RespostaDeSessao>('/api/auth/login', {
        metodo: 'POST',
        corpo: { email, senha },
        semAutenticacao: true,
      });

      aplicarSessao(sessao, () => void renovar());
    },
    [aplicarSessao, renovar],
  );

  const sair = useCallback(async () => {
    try {
      await requisitar<void>('/api/auth/logout', {
        metodo: 'POST',
        semAutenticacao: true,
      });
    } catch {
      // O servidor pode estar fora do ar. Ainda assim a sessao local termina:
      // deixar o usuario preso numa tela autenticada porque o logout falhou
      // seria pior do que encerrar de um lado so.
    } finally {
      encerrarLocalmente();
    }
  }, [encerrarLocalmente]);

  const valor = useMemo<Sessao>(
    () => ({
      usuario,
      carregando,
      autenticado: usuario !== null,
      entrar,
      sair,
    }),
    [usuario, carregando, entrar, sair],
  );

  return <ContextoDeSessao value={valor}>{children}</ContextoDeSessao>;
}
