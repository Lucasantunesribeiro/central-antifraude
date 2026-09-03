/**
 * Rota autenticada placeholder.
 *
 * A Fase 0 monta apenas a estrutura de rota protegida. As telas operacionais
 * - transacoes, alertas, casos, regras, backtest, auditoria - chegam nas
 * fases 3 a 10, cada uma junto da capacidade de backend correspondente.
 */
export function PaginaDoConsole() {
  return (
    <section className="pagina">
      <h1>Console operacional</h1>
      <p className="pagina__resumo">
        Area autenticada. As telas operacionais sao construidas junto das fases de
        dominio correspondentes.
      </p>
    </section>
  );
}
