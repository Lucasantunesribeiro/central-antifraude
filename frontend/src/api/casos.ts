import type { PrioridadeDeAlerta, SinalResumido, StatusDoAlerta } from './alertas';
import type { Decisao } from './risco';

/**
 * Contratos da investigação, espelhando o backend.
 *
 * O frontend não decide transição de estado e não decide quem pode agir. As
 * **ações permitidas vêm do servidor** em cada leitura do caso: a tela mostra
 * o que ele disser que é possível, e o servidor recusa de novo quando a ação
 * chega. Esconder botão não é autorização (CLAUDE.md seção 52) — são as duas
 * coisas, e não uma no lugar da outra.
 */

export type StatusDoCaso = 'Novo' | 'EmAnalise' | 'Resolvido';

export type ResultadoDaInvestigacao = 'FraudeConfirmada' | 'Legitima' | 'Inconclusiva';

export const ROTULO_DO_STATUS_DO_CASO: Record<StatusDoCaso, string> = {
  Novo: 'Novo',
  EmAnalise: 'Em análise',
  Resolvido: 'Resolvido',
};

/**
 * Rótulos do resultado humano.
 *
 * Não é a decisão do motor. Uma transação pode ter recebido `Revisar`
 * automaticamente e terminar como `Legítima` — isso é um falso positivo
 * legítimo, e não um defeito.
 */
export const ROTULO_DO_RESULTADO: Record<ResultadoDaInvestigacao, string> = {
  FraudeConfirmada: 'Fraude confirmada',
  Legitima: 'Legítima',
  Inconclusiva: 'Inconclusiva',
};

export const RESULTADOS: readonly ResultadoDaInvestigacao[] = [
  'FraudeConfirmada',
  'Legitima',
  'Inconclusiva',
];

export const STATUS_DO_CASO: readonly StatusDoCaso[] = [
  'Novo',
  'EmAnalise',
  'Resolvido',
];

/** Ações que o workspace pode oferecer. Lista fechada, igual à do backend. */
export type AcaoDoCaso =
  'assumir' | 'transferir' | 'associarAlerta' | 'adicionarNota' | 'resolver';

export interface CasoNaLista {
  id: string;
  titulo: string;
  status: StatusDoCaso;
  resultado: ResultadoDaInvestigacao | null;
  responsavelId: string | null;
  responsavelNome: string | null;
  quantidadeDeAlertas: number;
  maiorScore: number;
  maiorPrioridade: PrioridadeDeAlerta | null;
  abertoEm: string;
  atualizadoEm: string;
  versao: number;
}

export type TipoDeEventoDoCaso =
  | 'CasoAberto'
  | 'AlertaAssociado'
  | 'Atribuido'
  | 'Transferido'
  | 'NotaAdicionada'
  | 'Resolvido';

export interface EventoDoCaso {
  id: string;
  tipo: TipoDeEventoDoCaso;
  descricao: string;
  autorId: string;
  autorNome: string;
  referenciaId: string | null;
  ocorridoEm: string;
}

export interface NotaDoCaso {
  id: string;
  conteudo: string;
  autorId: string;
  autorNome: string;
  criadaEm: string;
}

export interface AlertaDoCaso {
  id: string;
  transacaoId: string;
  decisao: Decisao;
  score: number;
  prioridade: PrioridadeDeAlerta;
  status: StatusDoAlerta;
  criadoEm: string;
  identificadorExterno: string | null;
  valor: number | null;
  moeda: string | null;
  clienteExternoId: string | null;
  ocorridaEm: string | null;
  sinais: SinalResumido[];
}

export interface CasoDetalhado {
  id: string;
  titulo: string;
  status: StatusDoCaso;
  resultado: ResultadoDaInvestigacao | null;
  responsavelId: string | null;
  responsavelNome: string | null;
  abertoPorNome: string | null;
  resolvidoPorNome: string | null;
  abertoEm: string;
  atualizadoEm: string;
  resolvidoEm: string | null;
  versao: number;
  alertas: AlertaDoCaso[];
  timeline: EventoDoCaso[];
  notas: NotaDoCaso[];
  /** Vem do servidor. A tela não deduz nenhuma delas. */
  acoesPermitidas: AcaoDoCaso[];
}

export interface PaginaDeCasos {
  itens: CasoNaLista[];
  pagina: number;
  tamanho: number;
  total: number;
  totalDePaginas: number;
}

export interface FiltrosDeCasos {
  status: StatusDoCaso | '';
  resultado: ResultadoDaInvestigacao | '';
  semResponsavel: boolean;
  pagina: number;
}

export const FILTROS_DE_CASOS_INICIAIS: FiltrosDeCasos = {
  status: '',
  resultado: '',
  semResponsavel: false,
  pagina: 1,
};

export const TAMANHO_DA_PAGINA_DE_CASOS = 25;

/**
 * Monta a query string da listagem.
 *
 * Campo vazio é omitido, e não enviado em branco: o backend recusa valor fora
 * do vocabulário, e mandar `status=` transformaria "não filtrei" em erro.
 */
export function consultaDeCasos(filtros: FiltrosDeCasos): string {
  const parametros = new URLSearchParams({
    pagina: String(filtros.pagina),
    tamanho: String(TAMANHO_DA_PAGINA_DE_CASOS),
  });

  if (filtros.status) {
    parametros.set('status', filtros.status);
  }

  if (filtros.resultado) {
    parametros.set('resultado', filtros.resultado);
  }

  if (filtros.semResponsavel) {
    parametros.set('semResponsavel', 'true');
  }

  return parametros.toString();
}

/** O caso permite esta ação agora, segundo o servidor? */
export function permite(caso: CasoDetalhado, acao: AcaoDoCaso): boolean {
  return caso.acoesPermitidas.includes(acao);
}
