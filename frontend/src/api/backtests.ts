/**
 * Contratos dos backtests.
 *
 * **A tela não simula nada.** Ela pede uma execução, acompanha o status e
 * mostra o documento que o servidor apurou. O perfil candidato é montado no
 * backend a partir do rascunho já gravado — se a configuração viesse daqui,
 * o backtest seria um caminho para executar regra que nunca passou pelo
 * catálogo fechado (CLAUDE.md seção 21).
 *
 * **Nenhum número apurado é recalculado aqui.** As funções abaixo só derivam
 * rótulo e sinal aritmético a partir do que veio pronto: o frontend não é
 * autoridade sobre risco (CLAUDE.md seção 81).
 */

export type StatusDoBacktest =
  'Pendente' | 'Executando' | 'Concluida' | 'Falhou' | 'Cancelada';

export type Decisao = 'Permitir' | 'Revisar' | 'Bloquear';

export interface Distribuicao {
  permitir: number;
  revisar: number;
  bloquear: number;
}

export interface RegraCandidata {
  regraId: string;
  nome: string;
  tipo: string;
  configuracao: string;
  pontos: number;
  origem: 'Publicada' | 'Rascunho';
}

export interface PerfilCandidato {
  limiarDeRevisao: number;
  limiarDeBloqueio: number;
  regras: RegraCandidata[];
}

export interface Mudanca {
  de: Decisao;
  para: Decisao;
  quantidade: number;
}

export interface LinhaPorVeredito {
  /** Nulo é "sem resultado conhecido" — ausência de investigação. */
  veredito: 'FraudeConfirmada' | 'Legitima' | 'Inconclusiva' | null;
  total: number;
  vigente: Distribuicao;
  candidato: Distribuicao;
}

export interface FaixaDeScore {
  de: number;
  ate: number;
  vigente: number;
  candidato: number;
}

export interface ResultadoDoBacktest {
  totalAnalisado: number;
  totalQueAcionaria: number;
  totalDeMudancas: number;
  vigente: Distribuicao;
  candidato: Distribuicao;
  mudancas: Mudanca[];
  porVeredito: LinhaPorVeredito[];
  faixasDeScore: FaixaDeScore[];
}

export interface ExecucaoDeBacktest {
  id: string;
  descricao: string;
  regraCandidataId: string | null;
  status: StatusDoBacktest;
  inicio: string;
  fim: string;
  numeroDaVersaoDePerfilVigente: number;
  solicitadaPor: string;
  solicitadaEm: string;
  concluidaEm: string | null;
  totalAnalisado: number | null;
  totalDeMudancas: number | null;
  mensagemDeErro: string | null;
  /** Token de concorrência. Vai no cancelamento. */
  versao: number;
}

export interface BacktestDetalhado {
  execucao: ExecucaoDeBacktest;
  candidato: PerfilCandidato;
  resultado: ResultadoDoBacktest | null;
}

export interface PaginaDeBacktests {
  itens: ExecucaoDeBacktest[];
  pagina: number;
  tamanho: number;
  totalDeItens: number;
  totalDePaginas: number;
}

export const ROTULO_DO_STATUS: Record<StatusDoBacktest, string> = {
  Pendente: 'Na fila',
  Executando: 'Executando',
  Concluida: 'Concluída',
  Falhou: 'Falhou',
  Cancelada: 'Cancelada',
};

export const ROTULO_DO_VEREDITO: Record<string, string> = {
  FraudeConfirmada: 'Fraude confirmada',
  Legitima: 'Legítima',
  Inconclusiva: 'Inconclusiva',
};

/** "Sem resultado conhecido" é ausência de investigação, não um veredito. */
export function rotularVeredito(veredito: string | null): string {
  return veredito === null
    ? 'Sem investigação'
    : (ROTULO_DO_VEREDITO[veredito] ?? veredito);
}

/** Ainda vai mudar de estado sozinha? É o que decide se a tela repergunta. */
export function estaEmAndamento(status: StatusDoBacktest): boolean {
  return status === 'Pendente' || status === 'Executando';
}

/** Só o que ainda não terminou pode ser cancelado. */
export function podeCancelar(status: StatusDoBacktest): boolean {
  return estaEmAndamento(status);
}

/**
 * A mudança aperta ou afrouxa a decisão?
 *
 * Serve para colorir a linha, e é a diferença que o Supervisor está medindo:
 * mais bloqueio pega mais fraude e incomoda mais cliente legítimo.
 */
const SEVERIDADE: Record<Decisao, number> = {
  Permitir: 0,
  Revisar: 1,
  Bloquear: 2,
};

export function direcaoDaMudanca(mudanca: Mudanca): 'mais-rigido' | 'mais-permissivo' {
  return SEVERIDADE[mudanca.para] > SEVERIDADE[mudanca.de]
    ? 'mais-rigido'
    : 'mais-permissivo';
}

/**
 * Diferença de uma decisão entre os dois perfis, com sinal.
 *
 * Zero vira string vazia de propósito: uma coluna cheia de "0" esconde as
 * linhas que realmente mudaram.
 */
export function variacao(vigente: number, candidato: number): string {
  const diferenca = candidato - vigente;

  if (diferenca === 0) {
    return '';
  }

  return diferenca > 0 ? `+${diferenca}` : `${diferenca}`;
}
