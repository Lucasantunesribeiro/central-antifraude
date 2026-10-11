import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { EstadoDeCarregamento, EstadoDeErro } from '../componentes/Estados';
import { mensagemAmigavel } from '../api/erros';
import { useSaudeDaApi } from '../api/saude';
import { useSessao } from '../sessao/contextoDeSessao';

/**
 * Rota publica — a porta de entrada do produto.
 *
 * Antes esta tela abria com um diagnostico de infraestrutura, e quem chegava
 * pela primeira vez via o resultado de um health check antes de entender o que
 * o produto faz. Agora ela lidera com o problema resolvido e uma acao clara:
 * **entrar na demonstracao sem senha**. O diagnostico continua acessivel, mas
 * recolhido ao fim — util para operar, nao para apresentar.
 */
export function PaginaInicial() {
  const sessao = useSessao();
  const navegar = useNavigate();

  const [entrando, definirEntrando] = useState(false);
  const [erro, definirErro] = useState<unknown>(null);

  async function explorarDemonstracao() {
    definirErro(null);
    definirEntrando(true);

    try {
      await sessao.entrarComoDemo();
      navegar('/painel');
    } catch (falha) {
      definirErro(falha);
    } finally {
      definirEntrando(false);
    }
  }

  return (
    <section className="pagina pagina--larga">
      <header className="capa">
        <p className="capa__selo">Plataforma antifraude · demonstração de portfólio</p>
        <h1 className="capa__titulo">Central Antifraude</h1>
        <p className="capa__resumo">
          Recebe transações de pagamentos digitais, avalia o risco de forma
          <strong> determinística e explicável</strong>, decide na hora — Permitir,
          Revisar ou Bloquear — e entrega a investigação humana auditável de cada caso
          suspeito.
        </p>

        <div className="capa__acoes">
          {sessao.autenticado ? (
            <Link to="/painel" className="botao botao--principal botao--grande">
              Ir ao painel →
            </Link>
          ) : (
            <button
              type="button"
              className="botao botao--principal botao--grande"
              onClick={() => void explorarDemonstracao()}
              disabled={entrando}
            >
              {entrando ? 'Entrando...' : 'Explorar a demonstração →'}
            </button>
          )}
          <Link to="/entrar" className="botao botao--grande">
            Entrar com sua conta
          </Link>
        </div>

        {!sessao.autenticado ? (
          <p className="capa__nota">
            A demonstração entra sem senha, como <strong>Analista de Fraude</strong>: dá
            para ler o painel, a fila de alertas, as transações e investigar os casos.
            Gestão de regras e administração ficam fora desse perfil.
          </p>
        ) : null}

        {erro ? (
          <p className="capa__erro" role="alert">
            {mensagemAmigavel(erro)}
          </p>
        ) : null}
      </header>

      <section className="roteiro" aria-labelledby="titulo-roteiro">
        <h2 id="titulo-roteiro">O caminho de uma transação</h2>
        <ol className="roteiro__passos">
          <li className="roteiro__passo">
            <span className="roteiro__numero" aria-hidden="true">
              1
            </span>
            <h3>Chega e é avaliada</h3>
            <p>
              Cada transação passa por regras versionadas e recebe um score e uma
              decisão — sem caixa-preta, sem IA no cálculo.
            </p>
          </li>
          <li className="roteiro__passo">
            <span className="roteiro__numero" aria-hidden="true">
              2
            </span>
            <h3>Mostra por quê</h3>
            <p>
              O detalhe da transação lista os sinais que pesaram e quanto cada um somou.
              A explicação sobrevive mesmo depois de a regra mudar.
            </p>
          </li>
          <li className="roteiro__passo">
            <span className="roteiro__numero" aria-hidden="true">
              3
            </span>
            <h3>Vira fila de trabalho</h3>
            <p>
              O que precisa de gente vira alerta e caso. O painel prioriza o que está
              esperando há tempo demais.
            </p>
          </li>
          <li className="roteiro__passo">
            <span className="roteiro__numero" aria-hidden="true">
              4
            </span>
            <h3>É investigado por uma pessoa</h3>
            <p>
              O analista assume, registra notas e conclui — inclusive um{' '}
              <strong>falso positivo</strong>: o motor pediu Revisar, a pessoa concluiu
              Legítima, e as duas decisões ficam registradas.
            </p>
          </li>
        </ol>
      </section>

      <details className="diagnostico">
        <summary>Diagnóstico técnico da infraestrutura</summary>
        <DiagnosticoDaApi />
      </details>
    </section>
  );
}

/**
 * O antigo conteudo da raiz: prova, no navegador, que o frontend fala com a API.
 * Continua util para operar e depurar, por isso segue aqui — recolhido, e nao na
 * frente de quem veio conhecer o produto.
 */
function DiagnosticoDaApi() {
  const saude = useSaudeDaApi();

  if (saude.isPending) {
    return <EstadoDeCarregamento rotulo="Consultando a API..." />;
  }

  if (saude.isError) {
    return (
      <EstadoDeErro erro={saude.error} aoTentarDeNovo={() => void saude.refetch()} />
    );
  }

  return (
    <div className="tabela-rolavel">
      <table className="tabela">
        <caption className="tabela__legenda">
          Resposta de <code>/health/ready</code>
        </caption>
        <thead>
          <tr>
            <th scope="col">Componente</th>
            <th scope="col">Estado</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <th scope="row">API</th>
            <td>
              <Selo estado={saude.data.estado} />
            </td>
          </tr>
          {Object.entries(saude.data.componentes).map(([nome, estado]) => (
            <tr key={nome}>
              <th scope="row">{nome}</th>
              <td>
                <Selo estado={estado} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Selo({ estado }: { estado: string }) {
  const saudavel = estado === 'Healthy';

  return (
    // O texto carrega a informacao, nao so a cor: quem nao distingue verde de
    // vermelho precisa conseguir ler o estado mesmo assim.
    <span className={`selo ${saudavel ? 'selo--ok' : 'selo--falha'}`}>
      {saudavel ? 'Operacional' : estado}
    </span>
  );
}
