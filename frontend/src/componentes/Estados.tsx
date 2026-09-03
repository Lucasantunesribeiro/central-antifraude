import type { ReactNode } from 'react';
import { ErroDaApi, mensagemAmigavel } from '../api/erros';

/**
 * Os tres estados que toda tela de dados precisa saber mostrar.
 *
 * Ficam juntos de proposito: sao a mesma decisao de interface vista de tres
 * angulos, e mante-los no mesmo arquivo torna dificil implementar um e
 * esquecer os outros dois.
 */

export function EstadoDeCarregamento({
  rotulo = 'Carregando...',
}: {
  rotulo?: string;
}) {
  return (
    <div className="estado estado--carregando" role="status" aria-live="polite">
      <span className="estado__indicador" aria-hidden="true" />
      <span>{rotulo}</span>
    </div>
  );
}

export function EstadoVazio({
  titulo,
  descricao,
  acao,
}: {
  titulo: string;
  descricao?: string;
  acao?: ReactNode;
}) {
  return (
    <div className="estado estado--vazio">
      <p className="estado__titulo">{titulo}</p>
      {descricao ? <p className="estado__descricao">{descricao}</p> : null}
      {acao}
    </div>
  );
}

export function EstadoDeErro({
  erro,
  aoTentarDeNovo,
}: {
  erro: unknown;
  aoTentarDeNovo?: () => void;
}) {
  const daApi = erro instanceof ErroDaApi ? erro : undefined;
  const podeRepetir = daApi?.valeTentarDeNovo ?? true;

  return (
    <div className="estado estado--erro" role="alert">
      <p className="estado__titulo">{mensagemAmigavel(erro)}</p>

      {daApi?.idDeCorrelacao ? (
        // O analista precisa conseguir passar este codigo ao suporte: e ele
        // que localiza a requisicao exata no log do servidor.
        <p className="estado__descricao">
          Codigo para o suporte: <code>{daApi.idDeCorrelacao}</code>
        </p>
      ) : null}

      {aoTentarDeNovo && podeRepetir ? (
        <button type="button" className="botao" onClick={aoTentarDeNovo}>
          Tentar de novo
        </button>
      ) : null}
    </div>
  );
}
