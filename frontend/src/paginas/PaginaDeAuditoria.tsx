import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { requisitar } from '../api/clienteHttp';
import { rotularOperacao, type PaginaDeAuditoria as Pagina } from '../api/operacao';
import {
  EstadoDeCarregamento,
  EstadoDeErro,
  EstadoVazio,
} from '../componentes/Estados';

const TAMANHO_DA_PAGINA = 25;

/**
 * A trilha de auditoria.
 *
 * **Somente leitura, e cronológica.** Não há botão que altere ou apague — nem
 * rota. Uma trilha editável não prova nada: seria a primeira coisa a ser
 * ajustada quando o resultado incomodasse alguém (CLAUDE.md seção 67).
 *
 * Também não há ordenação configurável. A trilha só suporta uma leitura — a
 * sequência dos fatos — e ordenar por outra coisa a desmontaria.
 *
 * O que aparece aqui é o que já estava gravado: a trilha nunca recebeu senha,
 * hash, token nem payload completo. O campo *detalhe* carrega um resumo curto
 * do tipo "Perfil: Auditor → Analista", que é o "antes/depois" na forma que
 * não vaza.
 */
export function PaginaDeAuditoria() {
  const [operacao, definirOperacao] = useState('');
  const [pagina, definirPagina] = useState(1);

  const operacoes = useQuery({
    queryKey: ['auditoria', 'operacoes'],
    queryFn: ({ signal }) =>
      requisitar<string[]>('/api/auditoria/operacoes', { sinal: signal }),
  });

  const consulta = new URLSearchParams({
    pagina: String(pagina),
    tamanho: String(TAMANHO_DA_PAGINA),
  });

  if (operacao !== '') {
    consulta.set('operacao', operacao);
  }

  const trilha = useQuery({
    queryKey: ['auditoria', operacao, pagina],
    queryFn: ({ signal }) =>
      requisitar<Pagina>(`/api/auditoria?${consulta.toString()}`, { sinal: signal }),
  });

  return (
    <section className="pagina pagina--larga">
      <h1>Auditoria</h1>
      <p className="pagina__resumo">
        Registro somente-inserção das operações sensíveis desta organização: quem fez o
        quê e quando. Nada aqui pode ser alterado ou apagado pelo produto.
      </p>

      <div className="filtros">
        <label className="campo">
          <span className="campo__rotulo">Operação</span>
          <select
            value={operacao}
            onChange={(evento) => {
              definirPagina(1);
              definirOperacao(evento.target.value);
            }}
          >
            <option value="">Todas</option>
            {(operacoes.data ?? []).map((disponivel) => (
              <option key={disponivel} value={disponivel}>
                {rotularOperacao(disponivel)}
              </option>
            ))}
          </select>
        </label>
      </div>

      {trilha.isPending ? <EstadoDeCarregamento rotulo="Carregando trilha..." /> : null}

      {trilha.isError ? (
        <EstadoDeErro
          erro={trilha.error}
          aoTentarDeNovo={() => void trilha.refetch()}
        />
      ) : null}

      {trilha.isSuccess && trilha.data.itens.length === 0 ? (
        <EstadoVazio
          titulo="Nenhum registro para este filtro."
          descricao="A trilha guarda criação de usuário, credencial, publicação de regra, atribuição e resolução de caso, entre outras."
        />
      ) : null}

      {trilha.isSuccess && trilha.data.itens.length > 0 ? (
        <>
          <table className="tabela">
            <caption className="tabela__legenda">
              {trilha.data.totalDeItens} registro(s)
            </caption>
            <thead>
              <tr>
                <th scope="col">Quando</th>
                <th scope="col">Operação</th>
                <th scope="col">Autor</th>
                <th scope="col">Entidade</th>
                <th scope="col">Detalhe</th>
                <th scope="col">Correlação</th>
              </tr>
            </thead>
            <tbody>
              {trilha.data.itens.map((registro) => (
                <tr key={registro.id}>
                  <th scope="row">
                    {new Date(registro.ocorridoEm).toLocaleString('pt-BR')}
                  </th>
                  <td>{rotularOperacao(registro.operacao)}</td>
                  {/* Sem autor identificado é o caso legítimo de uma tentativa
                      de login recusada: não há a quem atribuir. */}
                  <td>{registro.autor ?? '—'}</td>
                  <td>{registro.entidade}</td>
                  <td>{registro.detalhe ?? '—'}</td>
                  <td>
                    {registro.idDeCorrelacao ? (
                      <code>{registro.idDeCorrelacao}</code>
                    ) : (
                      '—'
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          {trilha.data.totalDePaginas > 1 ? (
            <nav className="paginacao" aria-label="Paginação da trilha">
              <button
                type="button"
                className="botao"
                disabled={trilha.data.pagina <= 1}
                onClick={() => definirPagina(trilha.data.pagina - 1)}
              >
                Anterior
              </button>
              <span className="paginacao__posicao">
                Página {trilha.data.pagina} de {trilha.data.totalDePaginas}
              </span>
              <button
                type="button"
                className="botao"
                disabled={trilha.data.pagina >= trilha.data.totalDePaginas}
                onClick={() => definirPagina(trilha.data.pagina + 1)}
              >
                Próxima
              </button>
            </nav>
          ) : null}
        </>
      ) : null}
    </section>
  );
}
