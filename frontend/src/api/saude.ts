import { useQuery } from '@tanstack/react-query';
import { requisitar } from './clienteHttp';

/** Resposta de /health/ready, como o backend a escreve. */
export interface EstadoDeSaude {
  estado: 'Healthy' | 'Degraded' | 'Unhealthy';
  duracaoEmMs: number;
  componentes: Record<string, string>;
}

export const chaveDaSaude = ['saude', 'pronto'] as const;

/**
 * Unica chamada real de API que existe na Fase 0.
 *
 * Nao e uma tela de produto: e o diagnostico que prova que o frontend fala com
 * o backend e que os estados de carregando / erro / sucesso funcionam de
 * verdade, sem inventar um painel com dados falsos.
 */
export function useSaudeDaApi() {
  return useQuery({
    queryKey: chaveDaSaude,
    queryFn: ({ signal }) =>
      requisitar<EstadoDeSaude>('/health/ready', { sinal: signal }),
    // A politica de repeticao NAO e redefinida aqui de proposito: ela vive
    // em App.tsx, num lugar so. Um `retry` local sobrepoe a politica global
    // sem avisar, e foi exatamente isso que quebrou o teste de estado de erro.
    staleTime: 15_000,
  });
}
