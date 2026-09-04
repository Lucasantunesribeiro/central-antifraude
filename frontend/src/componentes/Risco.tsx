import {
  ROTULO_DA_DECISAO,
  rotularTipoDeRegra,
  type Avaliacao,
  type Decisao,
  type Sinal,
} from '../api/risco';

/**
 * Os elementos visuais do risco, reunidos.
 *
 * Duas regras de honestidade guiam este arquivo:
 *
 * 1. **A cor nunca é a única informação.** O selo de decisão sempre traz o
 *    texto junto. Quem não distingue verde de vermelho precisa ler a mesma
 *    coisa que os outros.
 * 2. **A tela não recalcula nada.** Score, decisão e pontos vêm prontos do
 *    servidor. O frontend não conhece limiar e não deduz decisão a partir do
 *    score — se deduzisse, a tela discordaria do backend no dia em que um
 *    perfil novo fosse publicado.
 */

/** Classe do selo por decisão. O texto vai sempre junto. */
const CLASSE_DA_DECISAO: Record<Decisao, string> = {
  Permitir: 'selo--permitir',
  Revisar: 'selo--revisar',
  Bloquear: 'selo--bloquear',
};

export function SeloDeDecisao({ decisao }: { decisao: Decisao | null }) {
  if (decisao === null) {
    return <span className="selo selo--ausente">sem avaliação</span>;
  }

  return (
    <span className={`selo ${CLASSE_DA_DECISAO[decisao]}`}>
      {ROTULO_DA_DECISAO[decisao]}
    </span>
  );
}

/**
 * O score como número e como barra.
 *
 * A barra é leitura periférica — o analista varre a coluna e vê onde estão os
 * casos pesados. O número continua ali porque é ele que vale.
 */
export function Score({ valor }: { valor: number | null }) {
  if (valor === null) {
    return <span className="score score--ausente">—</span>;
  }

  return (
    <span className="score" title={`Score ${valor} de 100`}>
      <span className="score__numero">{valor}</span>
      <span className="score__barra" aria-hidden="true">
        <span className="score__preenchimento" style={{ width: `${valor}%` }} />
      </span>
    </span>
  );
}

/**
 * Os sinais que produziram o score, com a contribuição de cada um.
 *
 * A versão da regra aparece em cada linha de propósito: é ela que permite
 * abrir uma avaliação antiga e entendê-la com a configuração daquele momento,
 * mesmo depois de a regra ter sido republicada (CLAUDE.md seção 17).
 */
export function ListaDeSinais({ sinais }: { sinais: Sinal[] }) {
  if (sinais.length === 0) {
    return (
      <p className="pagina__resumo">
        Nenhuma regra foi acionada. Nenhum sinal de risco foi produzido para esta
        transação.
      </p>
    );
  }

  return (
    <ul className="sinais">
      {sinais.map((sinal) => (
        <li key={`${sinal.regraId}-${sinal.versaoDaRegra}`} className="sinal">
          <div className="sinal__cabecalho">
            <span className="sinal__tipo">{rotularTipoDeRegra(sinal.tipo)}</span>
            <span className="sinal__pontos numerico">+{sinal.pontos}</span>
          </div>

          <p className="sinal__explicacao">{sinal.explicacao}</p>

          <dl className="sinal__evidencia">
            {Object.entries(sinal.evidencia).map(([chave, valor]) => (
              <div key={chave} className="sinal__par">
                <dt>{chave}</dt>
                <dd>{valor}</dd>
              </div>
            ))}
          </dl>

          <p className="sinal__origem">Regra versão {sinal.versaoDaRegra}</p>
        </li>
      ))}
    </ul>
  );
}

/**
 * O cabeçalho da avaliação: score, decisão e a procedência do resultado.
 *
 * Quando a soma bruta dos pontos passa de 100, a tela diz isso em voz alta.
 * Sem esse aviso, alguém somaria as contribuições exibidas, chegaria a 130 e
 * acharia que o sistema errou uma conta simples.
 */
export function ResumoDaAvaliacao({ avaliacao }: { avaliacao: Avaliacao }) {
  return (
    <div className="cartao avaliacao">
      <div className="avaliacao__resultado">
        <Score valor={avaliacao.score} />
        <SeloDeDecisao decisao={avaliacao.decisao} />
      </div>

      {avaliacao.scoreFoiLimitado ? (
        <p className="avaliacao__aviso">
          As regras somaram {avaliacao.somaBrutaDosPontos} pontos. O score é limitado a
          100.
        </p>
      ) : null}

      <dl className="avaliacao__procedencia">
        <div>
          <dt>Avaliada em</dt>
          <dd>{new Date(avaliacao.avaliadaEm).toLocaleString('pt-BR')}</dd>
        </div>
        <div>
          <dt>Versão do perfil</dt>
          <dd>{avaliacao.numeroDaVersaoDePerfil}</dd>
        </div>
        <div>
          <dt>Versão do motor</dt>
          <dd>{avaliacao.versaoDoMotor}</dd>
        </div>
      </dl>
    </div>
  );
}
