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

/** Teto de dias da janela do painel — o mesmo do backend (`JanelaDoPainel`). */
export const DIAS_MAXIMOS_DO_PAINEL = 90;

/**
 * Janela-alvo para o salto "ver o período com atividade", limitada ao teto.
 *
 * Usa `[max(primeira, última − teto), última]`: para o cenário da demonstração
 * (poucos dias) cobre tudo; para um tenant cujo histórico é mais longo que o
 * teto, cai nos últimos `DIAS_MAXIMOS_DO_PAINEL` dias de atividade. Em nenhum
 * caso excede o teto — então o botão nunca pede um intervalo que o backend
 * recusaria. Datas em ISO-8601 (UTC), prontas para a query.
 */
export function periodoComAtividade(
  primeira: string,
  ultima: string,
): { de: string; ate: string } {
  const fim = new Date(ultima);
  const tetoEmMs = DIAS_MAXIMOS_DO_PAINEL * 24 * 60 * 60 * 1000;
  const limiteInferior = fim.getTime() - tetoEmMs;
  const inicio = Math.max(new Date(primeira).getTime(), limiteInferior);

  return { de: new Date(inicio).toISOString(), ate: fim.toISOString() };
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
  /**
   * Extremos da atividade avaliada do tenant, independentes da janela. Nulos
   * quando o tenant nunca teve avaliação. Servem para oferecer um salto até o
   * período com dados quando a janela padrão (ancorada em "agora") abre vazia —
   * é o que impede a demonstração de parecer morta quando o cenário envelhece.
   */
  primeiraAtividadeEm: string | null;
  ultimaAtividadeEm: string | null;
  /** Muda só o rótulo do salto: demonstração vs. "ver atividade anterior". */
  periodoDeDemonstracao: boolean;
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
