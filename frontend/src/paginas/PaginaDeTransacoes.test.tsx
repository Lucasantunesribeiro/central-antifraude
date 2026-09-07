import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import { consultaDeTransacoes } from '../api/risco';
import { PaginaDeTransacoes } from './PaginaDeTransacoes';

/**
 * O console de transações.
 *
 * O que estes testes protegem é que **a tela não filtra nada**: ela monta a
 * query string e o servidor decide. Filtrar em memória daria um total que não
 * bate com a lista e páginas vazias no fim — e o backend recusa filtro
 * desconhecido com `400`, então inventar um aqui só produziria erro.
 */

function linha(indice: number, score: number | null) {
  return {
    id: `01a069e3-000${indice}-7da4-9b84-7bfdb54799ae`,
    identificadorExterno: `pedido-${indice}`,
    valor: 100 * indice,
    moeda: 'BRL',
    ocorridaEm: '2026-09-07T10:00:00Z',
    recebidaEm: '2026-09-07T10:00:01Z',
    clienteExternoId: `cli-${indice}`,
    paisDeOrigem: 'BR',
    score,
    decisao: score === null ? null : 'Permitir',
  };
}

const PAGINA = {
  itens: [linha(1, 10), linha(2, null)],
  pagina: 1,
  tamanho: 25,
  total: 60,
  totalDePaginas: 3,
};

function json(corpo: unknown) {
  return new Response(JSON.stringify(corpo), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function montar(rota = '/transacoes', pagina: unknown = PAGINA) {
  const urls: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) => {
      urls.push(url);

      return Promise.resolve(json(pagina));
    }),
  );

  render(
    <QueryClientProvider client={criarClienteDeConsultas()}>
      <MemoryRouter initialEntries={[rota]}>
        <PaginaDeTransacoes />
      </MemoryRouter>
    </QueryClientProvider>,
  );

  return urls;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('console de transações', () => {
  it('manda o filtro para o servidor em vez de aplicá-lo aqui', async () => {
    const urls = montar();

    await screen.findByRole('table');

    await userEvent.type(screen.getByLabelText('Buscar'), 'pedido');

    await waitFor(() =>
      expect(urls.some((u) => u.includes('busca=pedido'))).toBe(true),
    );

    // As duas linhas continuam ali: quem filtra é o backend, e a resposta
    // simulada não mudou. A tela não escondeu nada por conta própria.
    expect(within(await screen.findByRole('table')).getAllByRole('row')).toHaveLength(
      3,
    );
  });

  it('herda o filtro da URL, para o link do painel cair aqui já filtrado', async () => {
    const urls = montar('/transacoes?tipoDeRegra=NovoDispositivo&decisao=Bloquear');

    await waitFor(() =>
      expect(urls.some((u) => u.includes('tipoDeRegra=NovoDispositivo'))).toBe(true),
    );

    expect(urls[0]).toContain('decisao=Bloquear');
    expect(screen.getByLabelText('Acionou a regra')).toHaveValue('NovoDispositivo');
  });

  it('volta para a primeira página ao mudar o filtro', async () => {
    // Continuar na página 4 de um resultado que agora tem uma página mostraria
    // uma tela vazia e faria parecer que não há nada.
    const urls = montar();

    await screen.findByRole('table');

    await userEvent.click(screen.getByRole('button', { name: 'Próxima' }));
    await waitFor(() => expect(urls.some((u) => u.includes('pagina=2'))).toBe(true));

    await userEvent.selectOptions(screen.getByLabelText('Decisão'), 'Revisar');

    await waitFor(() =>
      expect(
        urls.some((u) => u.includes('decisao=Revisar') && u.includes('pagina=1')),
      ).toBe(true),
    );
  });

  it('alterna a direção ao clicar duas vezes na mesma coluna', async () => {
    const urls = montar();

    await screen.findByRole('table');

    await userEvent.click(screen.getByRole('button', { name: /^Score/ }));
    await waitFor(() =>
      expect(urls.some((u) => u.includes('ordenarPor=score&direcao=desc'))).toBe(true),
    );

    await userEvent.click(screen.getByRole('button', { name: /^Score/ }));
    await waitFor(() =>
      expect(urls.some((u) => u.includes('ordenarPor=score&direcao=asc'))).toBe(true),
    );
  });

  it('diz por qual coluna a lista está ordenada, e não só com a setinha', async () => {
    // Numa tabela operacional, saber a ordem muda a leitura de tudo abaixo.
    montar();

    const tabela = await screen.findByRole('table');
    const cabecalhos = within(tabela).getAllByRole('columnheader');

    const recebidaEm = cabecalhos.find((c) =>
      c.textContent?.startsWith('Recebida em'),
    )!;

    expect(recebidaEm).toHaveAttribute('aria-sort', 'descending');
  });

  it('mostra "sem avaliação" em vez de fingir score zero', async () => {
    montar();

    const tabela = await screen.findByRole('table');

    expect(within(tabela).getByText('sem avaliação')).toBeInTheDocument();
  });

  it('esconde a paginação quando só há uma página', async () => {
    montar('/transacoes', { ...PAGINA, total: 2, totalDePaginas: 1 });

    await screen.findByRole('table');

    expect(screen.queryByRole('navigation', { name: /Paginação/ })).toBeNull();
  });
});

describe('query string do console', () => {
  it('omite o que está em branco', () => {
    const consulta = consultaDeTransacoes(
      { busca: '', decisao: 'Revisar', scoreMinimo: undefined },
      2,
      25,
      'score',
      'asc',
    );

    expect(consulta).not.toContain('busca=');
    expect(consulta).not.toContain('scoreMinimo=');
    expect(consulta).toContain('decisao=Revisar');
    expect(consulta).toContain('pagina=2');
    expect(consulta).toContain('ordenarPor=score');
    expect(consulta).toContain('direcao=asc');
  });

  it('escapa o que o usuário digitou', () => {
    // O escape aqui é de URL, não de SQL: quem trata o curinga é o backend.
    // O que a tela garante é que o termo chega inteiro.
    const consulta = consultaDeTransacoes(
      { busca: 'a&b=c' },
      1,
      25,
      'recebidaEm',
      'desc',
    );

    expect(consulta).toContain('busca=a%26b%3Dc');
  });
});
