/**
 * Contratos do painel operacional, das métricas de regra e da trilha.
 *
 * **Nenhum número é calculado aqui.** O servidor conta; a tela apresenta. Uma
 * porcentagem derivada no frontend passaria a discordar do backend no primeiro
 * arredondamento diferente, e a tela viraria uma segunda fonte de verdade
 * (CLAUDE.md seções 44 e 81).
 */

import type { Decisao } from './risco';

export interface Contagem {
  chave: string;
  quantidade: number;
}

export interface DiaDoPainel {
  dia: string;
  permitir: number;
  revisar: number;
  bloquear: number;
  total: number;
}

export interface SinalFrequente {
  tipo: string;
  acionamentos: number;
}

export interface Painel {
  dias: number;
  inicio: string;
  fim: string;
  /** Quantas chegaram no período, por `recebidaEm`. */
  transacoesRecebidas: number;
  /** Quantas foram decididas no período, por `avaliadaEm`. */
  transacoesAvaliadas: number;
  decisoes: Contagem[];
  tendencia: DiaDoPainel[];
  sinaisMaisFrequentes: SinalFrequente[];
  alertasAbertos: number;
  casos: Contagem[];
  casosAntigos: number;
  diasParaCasoAntigo: number;
  /** Eventos que ainda não saíram da Outbox. Zero é o estado saudável. */
  eventosPendentes: number;
}

export interface MetricaDeRegra {
  regraId: string;
  nome: string;
  tipo: string;
  acionamentos: number;
  fraudeConfirmada: number;
  legitima: number;
  inconclusiva: number;
  semResultadoConhecido: number;
}

export interface RegistroDeAuditoria {
  id: string;
  operacao: string;
  autorId: string | null;
  autor: string | null;
  entidade: string;
  entidadeId: string | null;
  detalhe: string | null;
  idDeCorrelacao: string | null;
  ocorridoEm: string;
}

export interface PaginaDeAuditoria {
  itens: RegistroDeAuditoria[];
  pagina: number;
  tamanho: number;
  totalDeItens: number;
  totalDePaginas: number;
}

export const ROTULO_DO_STATUS_DE_CASO: Record<string, string> = {
  Novo: 'Novo',
  EmAnalise: 'Em análise',
  Resolvido: 'Resolvido',
};

/**
 * Quebra o nome da operação auditada em palavras legíveis.
 *
 * O vocabulário do enum é fechado e cresce a cada fase; um dicionário fixo
 * aqui ficaria desatualizado em silêncio e a tela mostraria a última operação
 * nova como um nome cru enquanto ninguém percebesse.
 */
export function rotularOperacao(operacao: string): string {
  const separado = operacao.replace(/([a-z])([A-Z])/g, '$1 $2');

  return separado.charAt(0).toUpperCase() + separado.slice(1).toLowerCase();
}

/** Quantas transações caíram numa decisão, no período. */
export function quantidadeDe(contagens: Contagem[], chave: string): number {
  return contagens.find((c) => c.chave === chave)?.quantidade ?? 0;
}

/**
 * Fatia de uma decisão no total, em porcentagem inteira.
 *
 * Derivar isto na tela é seguro porque não é um número do domínio: é a mesma
 * divisão que o leitor faria de cabeça. O que a tela nunca faz é inventar
 * contagem — o denominador vem do servidor.
 */
export function proporcao(quantidade: number, total: number): number {
  return total === 0 ? 0 : Math.round((quantidade / total) * 100);
}

/** Maior total diário da série, para dimensionar as barras da tendência. */
export function pico(tendencia: DiaDoPainel[]): number {
  return tendencia.reduce((maior, dia) => Math.max(maior, dia.total), 0);
}

export const DECISOES: readonly Decisao[] = ['Permitir', 'Revisar', 'Bloquear'];
