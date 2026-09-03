import { QueryClient } from '@tanstack/react-query';
import { ErroDaApi } from './erros';

/**
 * Politica de repeticao de requisicao, em um lugar so.
 *
 * O padrao do TanStack Query repete qualquer falha tres vezes. Isso esta
 * errado para esta API: repetir um 400 nunca vai dar certo, e repetir um 409
 * de idempotencia so gera ruido. Repetir faz sentido apenas em falha de rede
 * e em erro de servidor - o mesmo raciocinio que o CLAUDE.md secao 37 aplica
 * no backend.
 *
 * Nenhum hook deve redefinir `retry` localmente: um override silencioso
 * desfaz esta decisao sem que ninguem perceba.
 */
export function criarClienteDeConsultas(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: (tentativas, erro) => {
          if (erro instanceof ErroDaApi && !erro.valeTentarDeNovo) {
            return false;
          }
          return tentativas < 2;
        },
        refetchOnWindowFocus: false,
      },
      mutations: {
        // Nunca repetir escrita automaticamente: sem chave de idempotencia
        // no cliente, um retry as cegas pode duplicar um efeito.
        retry: false,
      },
    },
  });
}
