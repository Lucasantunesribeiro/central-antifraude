import { EstadoDeCarregamento, EstadoDeErro } from '../componentes/Estados';
import { useSaudeDaApi } from '../api/saude';

/**
 * Rota publica.
 *
 * Nao e uma landing page nem um painel: e a pagina de diagnostico da fundacao.
 * Ela existe para provar, no navegador, que o frontend fala com a API e que os
 * tres estados de carregamento funcionam contra um servico real.
 *
 * As telas de produto comecam na Fase 1.
 */
export function PaginaInicial() {
  const saude = useSaudeDaApi();

  return (
    <section className="pagina">
      <h1>Central Antifraude</h1>
      <p className="pagina__resumo">
        Plataforma de avaliacao de risco, monitoramento e investigacao de transacoes
        suspeitas em pagamentos digitais.
      </p>

      <section className="cartao" aria-labelledby="titulo-conectividade">
        <h2 id="titulo-conectividade">Conectividade com a API</h2>

        {saude.isPending ? (
          <EstadoDeCarregamento rotulo="Consultando a API..." />
        ) : null}

        {saude.isError ? (
          <EstadoDeErro
            erro={saude.error}
            aoTentarDeNovo={() => void saude.refetch()}
          />
        ) : null}

        {saude.isSuccess ? (
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
        ) : null}
      </section>
    </section>
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
