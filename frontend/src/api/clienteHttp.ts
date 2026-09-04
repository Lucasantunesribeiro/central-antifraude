import { ErroDaApi, categoriaPorStatus, type ProblemDetails } from './erros';

/**
 * Cliente HTTP unico do frontend.
 *
 * Concentra tres decisoes que nao podem variar por tela:
 * 1. toda falha vira ErroDaApi, com a mesma leitura de Problem Details;
 * 2. toda requisicao leva um identificador de correlacao, o que permite ligar
 *    "o botao que o analista clicou" a linha de log do servidor;
 * 3. toda requisicao tem prazo - sem isso uma tela fica girando para sempre
 *    quando o backend para de responder sem fechar a conexao.
 */

const BASE_DA_API: string = import.meta.env.VITE_API_BASE_URL ?? '';
const PRAZO_PADRAO_EM_MS = 15_000;

export const CABECALHO_DE_CORRELACAO = 'X-Correlation-Id';

/**
 * Access token da sessao atual.
 *
 * Vive em memoria, e so. Nunca em localStorage nem em sessionStorage: um XSS
 * na aplicacao leria qualquer um dos dois e levaria a sessao junto. Aqui, o
 * valor morre quando a aba fecha - e a continuidade entre recargas vem do
 * cookie HttpOnly, que o JavaScript nao enxerga.
 */
let tokenDeAcesso: string | null = null;

export function definirTokenDeAcesso(token: string | null): void {
  tokenDeAcesso = token;
}

export interface OpcoesDaRequisicao {
  metodo?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  corpo?: unknown;
  /** Cancelamento vindo de quem chamou (TanStack Query fornece o seu). */
  sinal?: AbortSignal;
  prazoEmMs?: number;
  /**
   * Nao envia o token de acesso. Usado pelas rotas de sessao, que se
   * autenticam pelo cookie ou pelo proprio corpo.
   */
  semAutenticacao?: boolean;
}

export async function requisitar<T>(
  caminho: string,
  opcoes: OpcoesDaRequisicao = {},
): Promise<T> {
  const {
    metodo = 'GET',
    corpo,
    sinal,
    prazoEmMs = PRAZO_PADRAO_EM_MS,
    semAutenticacao = false,
  } = opcoes;

  const prazo = AbortSignal.timeout(prazoEmMs);
  const cancelamento = sinal ? AbortSignal.any([sinal, prazo]) : prazo;

  const cabecalhos: Record<string, string> = {
    Accept: 'application/json',
    [CABECALHO_DE_CORRELACAO]: novoIdDeCorrelacao(),
  };

  if (corpo !== undefined) {
    cabecalhos['Content-Type'] = 'application/json';
  }

  if (!semAutenticacao && tokenDeAcesso !== null) {
    cabecalhos.Authorization = `Bearer ${tokenDeAcesso}`;
  }

  let resposta: Response;

  try {
    resposta = await fetch(`${BASE_DA_API}${caminho}`, {
      method: metodo,
      headers: cabecalhos,
      body: corpo === undefined ? undefined : JSON.stringify(corpo),
      signal: cancelamento,
      // Preparado para a Fase 1: a sessao humana usa cookie, entao a
      // credencial precisa acompanhar a requisicao.
      credentials: 'include',
    });
  } catch (causa) {
    throw new ErroDaApi('Falha de rede ao chamar a API.', 'rede', { causa });
  }

  if (!resposta.ok) {
    throw await interpretarFalha(resposta);
  }

  if (resposta.status === 204) {
    return undefined as T;
  }

  // Corpo vazio com 200: acontece em respostas sem conteudo declarado.
  const tipo = resposta.headers.get('Content-Type') ?? '';
  if (!tipo.includes('json')) {
    return undefined as T;
  }

  return (await resposta.json()) as T;
}

async function interpretarFalha(resposta: Response): Promise<ErroDaApi> {
  const problema = await lerProblemDetails(resposta);

  return new ErroDaApi(
    problema?.detail ?? problema?.title ?? `A API respondeu ${resposta.status}.`,
    categoriaPorStatus(resposta.status),
    {
      status: resposta.status,
      codigo: problema?.codigo,
      errosPorCampo: problema?.erros,
      // O cabecalho e a fonte mais confiavel: existe mesmo quando o corpo
      // veio vazio ou ilegivel.
      idDeCorrelacao:
        resposta.headers.get(CABECALHO_DE_CORRELACAO) ??
        problema?.idDeCorrelacao ??
        undefined,
    },
  );
}

async function lerProblemDetails(
  resposta: Response,
): Promise<ProblemDetails | undefined> {
  const tipo = resposta.headers.get('Content-Type') ?? '';

  if (!tipo.includes('json')) {
    return undefined;
  }

  try {
    return (await resposta.json()) as ProblemDetails;
  } catch {
    // Corpo prometido como JSON mas ilegivel. Nao ha o que fazer com isso, e
    // esconder o erro original atras de um erro de parse so atrapalharia.
    return undefined;
  }
}

function novoIdDeCorrelacao(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }

  // Ambientes sem crypto.randomUUID (jsdom antigo, http em rede local).
  // O backend aceita qualquer texto de 8 a 64 caracteres [A-Za-z0-9-_].
  return `web-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}
