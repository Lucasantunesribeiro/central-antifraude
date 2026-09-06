import type { PerfilDeUsuario } from './tipos';

/**
 * Contratos da administração de regras.
 *
 * **O formulário não sabe nada sobre regras.** Os campos, os limites e os
 * valores padrão vêm de `/api/regras/tipos`; a tela apenas desenha o que o
 * servidor descreveu. Se os limites estivessem repetidos aqui, sairiam de
 * sincronia no primeiro ajuste do backend — e a tela passaria a aceitar o que
 * o domínio recusa.
 *
 * O frontend também não decide o que vale: rascunho, versão publicada e perfil
 * vigente são estados que o servidor informa (CLAUDE.md seção 81).
 */

/** Natureza de um campo de configuração. Espelha o enum fechado do backend. */
export type TipoDoCampo = 'Inteiro' | 'Fracionario';

export interface CampoDeConfiguracao {
  nome: string;
  rotulo: string;
  tipo: TipoDoCampo;
  minimo: number;
  maximo: number;
  padrao: number;
}

export interface TipoDeRegraDisponivel {
  tipo: string;
  rotulo: string;
  resumo: string;
  pontosSugeridos: number;
  campos: CampoDeConfiguracao[];
}

export interface VersaoDeRegra {
  id: string;
  numero: number;
  pontos: number;
  configuracao: string;
  valores: Record<string, number>;
  publicadaEm: string;
}

export interface RascunhoDeRegra {
  pontos: number;
  configuracao: string;
  valores: Record<string, number>;
}

export interface RegraAdministrada {
  id: string;
  tipo: string;
  nome: string;
  ativa: boolean;
  /** Token de concorrência administrativa. Vai em toda ação. */
  versao: number;
  /**
   * Está valendo agora? É diferente de `ativa`: uma regra recém-criada está
   * ativa e ainda não vale para ninguém, porque nunca foi publicada.
   */
  noPerfilVigente: boolean;
  numeroDaVersaoVigente: number | null;
  pontosVigentes: number | null;
  configuracaoVigente: string | null;
  rascunho: RascunhoDeRegra | null;
  versoes: VersaoDeRegra[];
}

/** Perfis que administram regras. Repetido do backend só para a navegação. */
export const PERFIS_DE_SUPERVISAO: readonly PerfilDeUsuario[] = [
  'Administrador',
  'SupervisorDeFraude',
];

export function podeAdministrarRegras(perfil: PerfilDeUsuario | undefined): boolean {
  return perfil !== undefined && PERFIS_DE_SUPERVISAO.includes(perfil);
}

/** Situação de uma regra, em uma palavra, para a coluna de status da lista. */
export function situacaoDaRegra(regra: RegraAdministrada): string {
  if (!regra.ativa) {
    return 'Desativada';
  }

  if (regra.numeroDaVersaoVigente === null) {
    return 'Rascunho';
  }

  return regra.rascunho === null ? 'Em vigor' : 'Alterações pendentes';
}

/**
 * Valores iniciais de um formulário, a partir da descrição do tipo.
 *
 * Quando há rascunho, ele vence: é o que a pessoa estava escrevendo.
 */
export function valoresIniciais(
  tipo: TipoDeRegraDisponivel,
  existentes?: Record<string, number>,
): Record<string, number> {
  const valores: Record<string, number> = {};

  for (const campo of tipo.campos) {
    valores[campo.nome] = existentes?.[campo.nome] ?? campo.padrao;
  }

  return valores;
}

/**
 * O campo está dentro da faixa que o servidor declarou?
 *
 * Isto é conveniência de digitação, não autoridade: quem recusa de verdade é o
 * backend, com a mesma faixa. A tela avisa antes para a pessoa não perder o
 * que escreveu num 400.
 */
export function foraDaFaixa(campo: CampoDeConfiguracao, valor: number): boolean {
  if (Number.isNaN(valor)) {
    return true;
  }

  if (campo.tipo === 'Inteiro' && !Number.isInteger(valor)) {
    return true;
  }

  return valor < campo.minimo || valor > campo.maximo;
}
