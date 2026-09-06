import type { Decisao } from './risco';

/**
 * Contratos da fila operacional, espelhando o backend.
 *
 * O frontend não classifica alerta e não decide prioridade (CLAUDE.md seção
 * 81). A política vive no domínio, roda no consumidor e grava a prioridade no
 * alerta; a tela apenas apresenta. Se ela deduzisse a prioridade a partir da
 * decisão, discordaria do backend no dia em que a política ganhasse uma
 * versão nova — e o filtro passaria a esconder alertas que existem.
 */

/** Os dois níveis da política. Espelha o enum fechado do backend. */
export type PrioridadeDeAlerta = 'Media' | 'Alta';

/**
 * Situação do alerta.
 *
 * Os três respondem a mesma pergunta do ponto de vista de quem trabalha a
 * fila: **isto ainda é trabalho meu?** Quem move o alerta é sempre o caso.
 */
export type StatusDoAlerta = 'Aberto' | 'EmCaso' | 'Encerrado';

export const ROTULO_DA_PRIORIDADE: Record<PrioridadeDeAlerta, string> = {
  Alta: 'Alta',
  Media: 'Média',
};

export const ROTULO_DO_STATUS: Record<StatusDoAlerta, string> = {
  Aberto: 'Na fila',
  EmCaso: 'Em investigação',
  Encerrado: 'Encerrado',
};

export const SITUACOES: readonly StatusDoAlerta[] = ['Aberto', 'EmCaso', 'Encerrado'];

/** Ordem de gravidade, para a leitura da tela. Não é regra de negócio. */
export const PRIORIDADES: readonly PrioridadeDeAlerta[] = ['Alta', 'Media'];

export const DECISOES_QUE_ALERTAM: readonly Decisao[] = ['Revisar', 'Bloquear'];

export interface SinalResumido {
  tipo: string;
  pontos: number;
}

export interface Alerta {
  id: string;
  transacaoId: string;
  avaliacaoId: string;
  decisao: Decisao;
  score: number;
  prioridade: PrioridadeDeAlerta;
  status: StatusDoAlerta;
  /** A investigação que levou este alerta, quando existe. */
  casoId: string | null;
  /** Quando o motor decidiu. */
  avaliadaEm: string;
  /** Quando o alerta entrou na fila. A diferença é a latência do backbone. */
  criadoEm: string;
  versaoDaPolitica: number;
  /** No máximo três; os de maior peso primeiro. */
  principaisSinais: SinalResumido[];
  /** Quantos sinais a avaliação produziu no total. */
  totalDeSinais: number;
}

export interface PaginaDeAlertas {
  itens: Alerta[];
  pagina: number;
  tamanho: number;
  total: number;
  totalDePaginas: number;
}

/** Campos de ordenação aceitos pelo backend. A lista é fechada lá também. */
export type CampoDeOrdenacao = 'criadoEm' | 'score' | 'prioridade';

export interface FiltrosDaFila {
  decisao: Decisao | '';
  prioridade: PrioridadeDeAlerta | '';
  status: StatusDoAlerta | '';
  scoreMinimo: string;
  ordenarPor: CampoDeOrdenacao;
  direcao: 'asc' | 'desc';
  pagina: number;
}

export const FILTROS_INICIAIS: FiltrosDaFila = {
  decisao: '',
  prioridade: '',
  /**
   * A fila abre no que ainda é trabalho.
   *
   * Sem isto, alertas já investigados ficariam na tela para sempre e ela
   * deixaria de dizer o que precisa de gente — viraria histórico.
   */
  status: 'Aberto',
  scoreMinimo: '',
  ordenarPor: 'criadoEm',
  direcao: 'desc',
  pagina: 1,
};

export const TAMANHO_DA_PAGINA = 25;

/**
 * Monta a query string da consulta.
 *
 * Campo vazio é omitido, e não enviado em branco: o backend recusa valor fora
 * do vocabulário, e mandar `decisao=` transformaria "não filtrei" em erro.
 */
export function consultaDaFila(filtros: FiltrosDaFila): string {
  const parametros = new URLSearchParams({
    pagina: String(filtros.pagina),
    tamanho: String(TAMANHO_DA_PAGINA),
    ordenarPor: filtros.ordenarPor,
    direcao: filtros.direcao,
  });

  if (filtros.decisao) {
    parametros.set('decisao', filtros.decisao);
  }

  if (filtros.prioridade) {
    parametros.set('prioridade', filtros.prioridade);
  }

  if (filtros.status) {
    parametros.set('status', filtros.status);
  }

  if (filtros.scoreMinimo.trim() !== '') {
    parametros.set('scoreMinimo', filtros.scoreMinimo.trim());
  }

  return parametros.toString();
}

/**
 * Há quanto tempo o alerta está esperando.
 *
 * A idade é o que o analista usa para decidir o que pegar primeiro quando a
 * prioridade empata. Calculada a partir de `criadoEm`, e não de `avaliadaEm`:
 * o que interessa é há quanto tempo o trabalho está parado na fila.
 */
export function idadeDoAlerta(criadoEm: string, agora: Date = new Date()): string {
  const segundos = Math.max(
    0,
    Math.round((agora.getTime() - new Date(criadoEm).getTime()) / 1000),
  );

  if (segundos < 60) {
    return `${segundos}s`;
  }

  if (segundos < 3600) {
    return `${Math.floor(segundos / 60)}min`;
  }

  if (segundos < 86_400) {
    return `${Math.floor(segundos / 3600)}h`;
  }

  return `${Math.floor(segundos / 86_400)}d`;
}
