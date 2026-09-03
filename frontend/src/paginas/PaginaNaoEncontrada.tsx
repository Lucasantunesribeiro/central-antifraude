import { Link } from 'react-router';

export function PaginaNaoEncontrada() {
  return (
    <section className="pagina">
      <h1>Pagina nao encontrada</h1>
      <p className="pagina__resumo">O endereco acessado nao existe.</p>
      <Link to="/">Voltar ao inicio</Link>
    </section>
  );
}
