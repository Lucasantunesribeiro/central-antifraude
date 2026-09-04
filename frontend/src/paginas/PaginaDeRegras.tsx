import { useQuery } from '@tanstack/react-query';
import { requisitar } from '../api/clienteHttp';
import { rotularTipoDeRegra, type PerfilVigente } from '../api/risco';
import { EstadoDeCarregamento, EstadoDeErro } from '../componentes/Estados';

/**
 * O catálogo de regras em vigor e os limiares que traduzem score em decisão.
 *
 * Somente leitura. Rascunho, backtest e publicação são a Fase 8 — e é
 * justamente essa sequência que protege a mudança de regra (CLAUDE.md seção
 * 23). Um botão de editar aqui seria um caminho para alterar o
 * comportamento do motor sem nenhuma dessas etapas.
 *
 * Os números são configuração de demonstração deste projeto, e a tela diz
 * isso: apresentá-los como padrão de mercado seria inventar (seção 112).
 */
export function PaginaDeRegras() {
  const consulta = useQuery({
    queryKey: ['regras', 'perfil'],
    queryFn: ({ signal }) =>
      requisitar<PerfilVigente>('/api/regras/perfil', { sinal: signal }),
  });

  return (
    <section className="pagina pagina--larga">
      <h1>Regras</h1>
      <p className="pagina__resumo">
        Catálogo fechado e tipado de regras. Cada regra produz um sinal explicável e
        soma uma contribuição ao score.
      </p>

      {consulta.isPending ? (
        <EstadoDeCarregamento rotulo="Carregando regras..." />
      ) : null}

      {consulta.isError ? (
        <EstadoDeErro
          erro={consulta.error}
          aoTentarDeNovo={() => void consulta.refetch()}
        />
      ) : null}

      {consulta.isSuccess ? (
        <>
          <div className="cartao">
            <h2>Perfil de risco — versão {consulta.data.numero}</h2>

            <ul className="faixas">
              <li className="faixa">
                <span className="selo selo--permitir">Permitir</span>
                <span className="faixa__intervalo">
                  0 a {consulta.data.limiarDeRevisao - 1}
                </span>
              </li>
              <li className="faixa">
                <span className="selo selo--revisar">Revisar</span>
                <span className="faixa__intervalo">
                  {consulta.data.limiarDeRevisao} a {consulta.data.limiarDeBloqueio - 1}
                </span>
              </li>
              <li className="faixa">
                <span className="selo selo--bloquear">Bloquear</span>
                <span className="faixa__intervalo">
                  {consulta.data.limiarDeBloqueio} a 100
                </span>
              </li>
            </ul>

            <p className="pagina__resumo">
              Publicado em {new Date(consulta.data.publicadaEm).toLocaleString('pt-BR')}
              . Os limiares e os pesos são configuração de demonstração deste projeto —
              não são padrão de mercado nem recomendação oficial.
            </p>
          </div>

          <table className="tabela">
            <caption className="tabela__legenda">
              {consulta.data.regras.length} regra(s) em vigor
            </caption>
            <thead>
              <tr>
                <th scope="col">Regra</th>
                <th scope="col">Configuração</th>
                <th scope="col">Pontos</th>
                <th scope="col">Versão</th>
              </tr>
            </thead>
            <tbody>
              {consulta.data.regras.map((regra) => (
                <tr key={regra.id}>
                  <th scope="row">{rotularTipoDeRegra(regra.tipo)}</th>
                  <td>{regra.configuracao}</td>
                  <td className="numerico">+{regra.pontos}</td>
                  <td className="numerico">{regra.versaoAtual}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      ) : null}
    </section>
  );
}
