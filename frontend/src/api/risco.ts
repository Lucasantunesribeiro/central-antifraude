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

/** Um alerta gerado pela transação, com o caso que o recolheu. */
export interface AlertaDaTransacao {
  alertaId: string;
  prioridade: string;
  status: string;
  criadoEm: string;
  casoId: string | null;
  tituloDoCaso: string | null;
  statusDoCaso: string | null;
}

/** A conclusão humana sobre a transação. Pode contradizer a decisão do motor. */
export type Veredito = 'FraudeConfirmada' | 'Legitima' | 'Inconclusiva';

export const ROTULO_DO_VEREDITO: Record<string, string> = {
  FraudeConfirmada: 'Fraude confirmada',
  Legitima: 'Legítima',
  Inconclusiva: 'Inconclusiva',
};

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
  /**
   * Número de protocolo da requisição que registrou a transação.
   *
   * É o que o suporte usa para achar a linha exata no log do servidor. Nulo
   * nas transações gravadas antes da Fase 10 — inventar um valor para elas
   * seria pior do que admitir a ausência.
   */
  idDeCorrelacao: string | null;
  avaliacao: Avaliacao | null;
  alertas: AlertaDaTransacao[];
  veredito: Veredito | null;
  casoDoVeredito: string | null;
  vereditoRegistradoEm: string | null;
}

/**
 * O que o console de transações aceita filtrar.
 *
 * Espelha o contrato do backend, e nada mais: a tela não inventa filtro que o
 * servidor não conhece, porque um filtro desconhecido é recusado com `400` em
 * vez de ignorado (CLAUDE.md seção 81).
 */
export interface FiltroDeTransacoes {
  busca?: string;
  decisao?: string;
  tipoDeRegra?: string;
  scoreMinimo?: string;
  scoreMaximo?: string;
  de?: string;
  ate?: string;
}

/** Monta a query string do console, omitindo o que está em branco. */
export function consultaDeTransacoes(
  filtro: FiltroDeTransacoes,
  pagina: number,
  tamanho: number,
  ordenarPor: string,
  direcao: 'asc' | 'desc',
): string {
  const parametros = new URLSearchParams();

  for (const [chave, valor] of Object.entries(filtro)) {
    if (valor !== undefined && valor !== '') {
      parametros.set(chave, valor);
    }
  }

  parametros.set('pagina', String(pagina));
  parametros.set('tamanho', String(tamanho));
  parametros.set('ordenarPor', ordenarPor);
  parametros.set('direcao', direcao);

  return parametros.toString();
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
