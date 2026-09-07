import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import { PaginaDeAuditoria } from './PaginaDeAuditoria';

/**
 * A trilha de auditoria.
 *
 * Duas promessas são verificadas aqui:
 *
 * 1. **a tela é somente leitura** — não há botão que altere ou apague, porque
 *    uma trilha editável não prova nada;
 * 2. **a leitura é cronológica e não configurável** — a trilha só suporta uma
 *    leitura, a sequência dos fatos, e ordenar por outra coisa a desmontaria.
 */

const PAGINA = {
  itens: [
    {
      id: '01a069e3-1111-7da4-9b84-7bfdb54799ae',
      operacao: 'VersaoDeRegraPublicada',
      autorId: '01a069e3-2222-7da4-9b84-7bfdb54799ae',
      autor: 'Supervisora de Teste',
      entidade: 'Regra',
      entidadeId: '01a069e3-3333-7da4-9b84-7bfdb54799ae',
      detalhe: 'Velocidade por cliente — Versao 2 com 30 ponto(s)',
      idDeCorrelacao: 'req-000000000001',
      ocorridoEm: '2026-09-07T12:00:00Z',
    },
    {
      id: '01a069e3-4444-7da4-9b84-7bfdb54799ae',
      operacao: 'LoginRecusado',
      autorId: null,
      autor: null,
      entidade: 'Usuario',
      entidadeId: null,
      detalhe: 'Credenciais invalidas',
      idDeCorrelacao: null,
      ocorridoEm: '2026-09-07T11:00:00Z',
    },
  ],
  pagina: 1,
  tamanho: 25,
  totalDeItens: 2,
  totalDePaginas: 1,
};

function json(corpo: unknown, status = 200) {
  return new Response(JSON.stringify(corpo), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function montar(pagina: unknown = PAGINA) {
  const urls: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) => {
      urls.push(url);

      if (url.includes('/api/auditoria/operacoes')) {
        return Promise.resolve(json(['VersaoDeRegraPublicada', 'LoginRecusado']));
      }

      return Promise.resolve(json(pagina));
    }),
  );

  render(
    <QueryClientProvider client={criarClienteDeConsultas()}>
      <MemoryRouter initialEntries={['/auditoria']}>
        <PaginaDeAuditoria />
      </MemoryRouter>
    </QueryClientProvider>,
  );

  return urls;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('trilha de auditoria', () => {
  it('mostra quem fez o quê e quando', async () => {
    montar();

    const tabela = await screen.findByRole('table');

    expect(within(tabela).getByText('Versao de regra publicada')).toBeInTheDocument();
    expect(within(tabela).getByText('Supervisora de Teste')).toBeInTheDocument();
    expect(
      within(tabela).getByText('Velocidade por cliente — Versao 2 com 30 ponto(s)'),
    ).toBeInTheDocument();
  });

  it('aceita registro sem autor sem quebrar a linha', async () => {
    // Uma tentativa de login recusada não tem a quem atribuir, e isso é
    // legítimo — a trilha registra o fato mesmo assim.
    montar();

    const tabela = await screen.findByRole('table');
    const linhas = within(tabela).getAllByRole('row').slice(1);

    expect(linhas[1]).toHaveTextContent('Login recusado');
    expect(linhas[1]).toHaveTextContent('—');
  });

  it('não oferece nenhum caminho para alterar a trilha', async () => {
    // A ausência de rota é a garantia; a ausência de botão é a consequência.
    montar();

    const tabela = await screen.findByRole('table');

    expect(within(tabela).queryByRole('button')).toBeNull();
    expect(within(tabela).queryByRole('textbox')).toBeNull();
    expect(screen.queryByRole('button', { name: /apagar|excluir|editar/i })).toBeNull();
  });

  it('não oferece ordenação: a trilha é cronológica', async () => {
    montar();

    const tabela = await screen.findByRole('table');

    for (const cabecalho of within(tabela).getAllByRole('columnheader')) {
      expect(cabecalho).not.toHaveAttribute('aria-sort');
      expect(within(cabecalho).queryByRole('button')).toBeNull();
    }
  });

  it('filtra por operação usando só o que existe na trilha', async () => {
    // Oferecer trinta operações das quais quatro têm registro faz a pessoa
    // procurar no vazio.
    const urls = montar();

    const seletor = await screen.findByLabelText('Operação');

    // A lista de operações é uma segunda consulta: esperar por ela é o que
    // torna o teste independente da ordem em que as duas respondem.
    await within(seletor).findByRole('option', { name: 'Versao de regra publicada' });

    const opcoes = within(seletor)
      .getAllByRole('option')
      .map((o) => o.textContent);

    expect(opcoes).toEqual(['Todas', 'Versao de regra publicada', 'Login recusado']);

    await userEvent.selectOptions(seletor, 'VersaoDeRegraPublicada');

    await waitFor(() =>
      expect(urls.some((u) => u.includes('operacao=VersaoDeRegraPublicada'))).toBe(
        true,
      ),
    );
  });

  it('diz que não há registro em vez de mostrar tabela vazia', async () => {
    montar({ ...PAGINA, itens: [], totalDeItens: 0 });

    expect(
      await screen.findByText('Nenhum registro para este filtro.'),
    ).toBeInTheDocument();
  });
});
