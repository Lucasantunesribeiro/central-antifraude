/**
 * Contratos de risco, espelhando o backend.
 *
 * O frontend NÃO calcula score e NÃO decide fraude (CLAUDE.md seção 81).
 * Tudo aqui é leitura: o servidor produziu o número, a decisão e a
 * explicação; a tela apenas apresenta. Não há nesta camada nenhum limiar
 * replicado — se houvesse, ele sairia de sincronia com o perfil publicado e a
 * tela passaria a mentir.
 */

/** As três decisões de risco do produto. Espelha o enum fechado do backend. */
export type Decisao = 'Permitir' | 'Revisar' | 'Bloquear';

/**
 * Rótulos em português.
 *
 * São recomendações de risco, nunca resultado financeiro: a Central
 * Antifraude não autoriza, não captura e não liquida (CLAUDE.md seção 10).
 */
export const ROTULO_DA_DECISAO: Record<Decisao, string> = {
  Permitir: 'Permitir',
  Revisar: 'Revisar',
  Bloquear: 'Bloquear',
};

/** Nomes legíveis dos tipos de regra do catálogo fechado. */
export const ROTULO_DO_TIPO_DE_REGRA: Record<string, string> = {
  VelocidadePorCliente: 'Velocidade por cliente',
  NovoDispositivo: 'Dispositivo novo',
  ValorAcimaDoHistorico: 'Valor acima do histórico',
  DivergenciaGeografica: 'Divergência geográfica',
};

export function rotularTipoDeRegra(tipo: string): string {
  return ROTULO_DO_TIPO_DE_REGRA[tipo] ?? tipo;
}

export interface Sinal {
  tipo: string;
  explicacao: string;
  pontos: number;
  regraId: string;
  versaoDaRegra: number;
  evidencia: Record<string, string>;
}

export interface Avaliacao {
  id: string;
  score: number;
  decisao: Decisao;
  avaliadaEm: string;
  versaoDePerfilId: string;
  numeroDaVersaoDePerfil: number;
  versaoDoMotor: string;
  somaBrutaDosPontos: number;
  /** Verdadeiro quando as regras somaram mais de 100 e o score foi limitado. */
  scoreFoiLimitado: boolean;
  sinais: Sinal[];
}

export interface TransacaoDaLista {
  id: string;
  identificadorExterno: string;
  valor: number;
  moeda: string;
  ocorridaEm: string;
  recebidaEm: string;
  clienteExternoId: string;
  paisDeOrigem: string | null;
  /**
   * Nulos para transações registradas antes da Fase 3, que existem sem
   * avaliação. A tela mostra "sem avaliação" — inventar score zero seria
   * apresentar uma decisão que ninguém tomou.
   */
  score: number | null;
  decisao: Decisao | null;
}

export interface TransacaoDetalhada {
  id: string;
  identificadorExterno: string;
  valor: number;
  moeda: string;
  ocorridaEm: string;
  recebidaEm: string;
  clienteExternoId: string;
  referenciaDoInstrumento: string;
  fingerprintDoDispositivo: string | null;
  paisDeOrigem: string | null;
  avaliacao: Avaliacao | null;
}

export interface Regra {
  id: string;
  tipo: string;
  nome: string;
  versaoAtual: number;
  pontos: number;
  configuracao: string;
  publicadaEm: string;
}

export interface PerfilVigente {
  versaoId: string;
  numero: number;
  limiarDeRevisao: number;
  limiarDeBloqueio: number;
  publicadaEm: string;
  regras: Regra[];
}
