/**
 * Contrato de erro da API, do lado do cliente.
 *
 * A API responde toda falha em Problem Details (RFC 9457). Traduzir isso uma
 * unica vez, aqui, evita que cada tela invente sua propria forma de descobrir
 * "o que deu errado" a partir de um objeto solto.
 */

/** Resposta de erro da API, no formato Problem Details. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  /** Codigo estavel definido pelo backend. Ex.: "validacao_falhou". */
  codigo?: string;
  /** Erros por campo, presentes quando codigo === "validacao_falhou". */
  erros?: Record<string, string[]>;
  /** Identificador que liga esta falha aos logs do servidor. */
  idDeCorrelacao?: string;
}

/** Categorias que a interface trata de forma diferente. */
export type CategoriaDeErro =
  | 'rede'
  | 'validacao'
  | 'naoAutenticado'
  | 'naoAutorizado'
  | 'naoEncontrado'
  | 'conflito'
  | 'limiteDeRequisicoes'
  | 'servidor';

export class ErroDaApi extends Error {
  readonly categoria: CategoriaDeErro;
  readonly status: number | undefined;
  readonly codigo: string | undefined;
  readonly errosPorCampo: Record<string, string[]> | undefined;
  readonly idDeCorrelacao: string | undefined;

  constructor(
    mensagem: string,
    categoria: CategoriaDeErro,
    detalhes: {
      status?: number;
      codigo?: string;
      errosPorCampo?: Record<string, string[]>;
      idDeCorrelacao?: string;
      causa?: unknown;
    } = {},
  ) {
    super(mensagem, { cause: detalhes.causa });
    this.name = 'ErroDaApi';
    this.categoria = categoria;
    this.status = detalhes.status;
    this.codigo = detalhes.codigo;
    this.errosPorCampo = detalhes.errosPorCampo;
    this.idDeCorrelacao = detalhes.idDeCorrelacao;
  }

  /** Repetir a chamada pode resolver? Falha de rede e 5xx sim; 400 nao. */
  get valeTentarDeNovo(): boolean {
    return this.categoria === 'rede' || this.categoria === 'servidor';
  }
}

export function categoriaPorStatus(status: number): CategoriaDeErro {
  switch (status) {
    case 400:
      return 'validacao';
    case 401:
      return 'naoAutenticado';
    case 403:
      return 'naoAutorizado';
    case 404:
      return 'naoEncontrado';
    case 409:
      return 'conflito';
    case 429:
      return 'limiteDeRequisicoes';
    default:
      return 'servidor';
  }
}

/** Mensagem curta e em portugues para o usuario final. */
export function mensagemAmigavel(erro: unknown): string {
  if (!(erro instanceof ErroDaApi)) {
    return 'Ocorreu uma falha inesperada.';
  }

  switch (erro.categoria) {
    case 'rede':
      return 'Nao foi possivel falar com o servidor. Verifique a conexao.';
    case 'validacao':
      return erro.message || 'Os dados enviados sao invalidos.';
    case 'naoAutenticado':
      return 'Sua sessao expirou. Entre novamente.';
    case 'naoAutorizado':
      return 'Seu perfil nao tem permissao para esta acao.';
    case 'naoEncontrado':
      return 'O item procurado nao foi encontrado.';
    case 'conflito':
      return erro.message || 'A operacao conflita com o estado atual.';
    case 'limiteDeRequisicoes':
      return 'Muitas requisicoes em pouco tempo. Aguarde um instante.';
    default:
      return 'O servidor nao conseguiu concluir a operacao.';
  }
}
