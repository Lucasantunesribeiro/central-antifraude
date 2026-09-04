import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import { definirTokenDeAcesso } from '../api/clienteHttp';
import { PaginaDeIntegracoes } from './PaginaDeIntegracoes';

/**
 * A tela de integrações guarda uma promessa que o servidor não pode desfazer:
 * a chave aparece uma única vez. Se a interface a escondesse, o administrador
 * ficaria sem ela; se a mostrasse de novo, estaria mentindo — porque só o
 * hash existe do outro lado.
 */

const INTEGRACAO = {
  id: '01a069e3-9970-7da4-9b84-7bfdb54799ae',
  nome: 'Checkout web',
  ativa: true,
  criadaEm: '2026-09-03T20:00:00Z',
};

const CREDENCIAL_EMITIDA = {
  id: '01a069e3-aaaa-7da4-9b84-7bfdb54799ae',
  identificadorPublico: '2d580e981df61c7a',
  chave: 'caf_2d580e981df61c7a_segredo-que-so-aparece-uma-vez',
  criadaEm: '2026-09-03T20:00:00Z',
  aviso: 'Guarde esta chave agora: ela nao sera exibida novamente.',
};

function json(corpo: unknown, status = 200) {
  return new Response(JSON.stringify(corpo), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Roteador de mock por caminho e método. */
function fetchDeMock(
  opcoes: { lista?: unknown; detalhe?: unknown; criacao?: unknown } = {},
) {
  return vi.fn((url: string, init?: RequestInit) => {
    const caminho = String(url);
    const metodo = init?.method ?? 'GET';

    if (caminho.includes('/api/integracoes/') && metodo === 'GET') {
      return Promise.resolve(
        json(opcoes.detalhe ?? { ...INTEGRACAO, credenciais: [] }),
      );
    }

    if (caminho.includes('/api/integracoes') && metodo === 'POST') {
      return Promise.resolve(
        json(
          opcoes.criacao ?? { integracao: INTEGRACAO, credencial: CREDENCIAL_EMITIDA },
          201,
        ),
      );
    }

    return Promise.resolve(json(opcoes.lista ?? { itens: [INTEGRACAO], total: 1 }));
  });
}

function montar() {
  return render(
    <QueryClientProvider client={criarClienteDeConsultas()}>
      <PaginaDeIntegracoes />
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  definirTokenDeAcesso(null);
});

describe('PaginaDeIntegracoes', () => {
  it('lista as integrações da organização', async () => {
    vi.stubGlobal('fetch', fetchDeMock());

    montar();

    expect(
      await screen.findByRole('rowheader', { name: 'Checkout web' }),
    ).toBeInTheDocument();
    expect(screen.getByText('Ativa')).toBeInTheDocument();
  });

  it('mostra estado vazio quando não há integração', async () => {
    vi.stubGlobal('fetch', fetchDeMock({ lista: { itens: [], total: 0 } }));

    montar();

    expect(await screen.findByText('Nenhuma integração ainda.')).toBeInTheDocument();
  });

  it('exibe a chave emitida ao criar a integração', async () => {
    // É a única vez que este valor existe fora do integrador.
    vi.stubGlobal('fetch', fetchDeMock());

    montar();
    await screen.findByRole('rowheader', { name: 'Checkout web' });

    await userEvent.type(screen.getByLabelText('Nome'), 'Checkout web');
    await userEvent.click(screen.getByRole('button', { name: /Criar e emitir chave/ }));

    const alerta = await screen.findByRole('alert');

    expect(alerta).toHaveTextContent(CREDENCIAL_EMITIDA.chave);
    expect(alerta).toHaveTextContent('nao sera exibida novamente');
  });

  it('a chave some da tela quando o administrador confirma que a guardou', async () => {
    vi.stubGlobal('fetch', fetchDeMock());

    montar();
    await screen.findByRole('rowheader', { name: 'Checkout web' });

    await userEvent.type(screen.getByLabelText('Nome'), 'Checkout web');
    await userEvent.click(screen.getByRole('button', { name: /Criar e emitir chave/ }));
    await screen.findByRole('alert');

    await userEvent.click(screen.getByRole('button', { name: 'Já guardei' }));

    await waitFor(() =>
      expect(screen.queryByText(CREDENCIAL_EMITIDA.chave)).not.toBeInTheDocument(),
    );
  });

  it('a listagem de credenciais mostra o identificador público e nunca o segredo', async () => {
    // O identificador público precisa aparecer - é como o administrador
    // reconhece qual chave está revogando. O segredo, nunca.
    vi.stubGlobal(
      'fetch',
      fetchDeMock({
        detalhe: {
          ...INTEGRACAO,
          credenciais: [
            {
              id: CREDENCIAL_EMITIDA.id,
              identificadorPublico: CREDENCIAL_EMITIDA.identificadorPublico,
              criadaEm: '2026-09-03T20:00:00Z',
              usadaPelaUltimaVezEm: null,
              revogadaEm: null,
              motivoDaRevogacao: null,
            },
          ],
        },
      }),
    );

    montar();
    await screen.findByRole('rowheader', { name: 'Checkout web' });

    await userEvent.click(screen.getByRole('button', { name: 'Ver chaves' }));

    expect(
      await screen.findByRole('rowheader', {
        name: CREDENCIAL_EMITIDA.identificadorPublico,
      }),
    ).toBeInTheDocument();

    expect(screen.queryByText(/segredo-que-so-aparece/)).not.toBeInTheDocument();
    expect(screen.getByText('nunca usada')).toBeInTheDocument();
  });

  it('credencial revogada aparece com o motivo e sem botão de revogar', async () => {
    vi.stubGlobal(
      'fetch',
      fetchDeMock({
        detalhe: {
          ...INTEGRACAO,
          credenciais: [
            {
              id: CREDENCIAL_EMITIDA.id,
              identificadorPublico: CREDENCIAL_EMITIDA.identificadorPublico,
              criadaEm: '2026-09-03T20:00:00Z',
              usadaPelaUltimaVezEm: '2026-09-03T21:00:00Z',
              revogadaEm: '2026-09-03T22:00:00Z',
              motivoDaRevogacao: 'RevogadaManualmente',
            },
          ],
        },
      }),
    );

    montar();
    await screen.findByRole('rowheader', { name: 'Checkout web' });
    await userEvent.click(screen.getByRole('button', { name: 'Ver chaves' }));

    expect(await screen.findByText('RevogadaManualmente')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Revogar' })).not.toBeInTheDocument();
  });

  it('mostra o erro quando a criação falha', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn((_url: string, init?: RequestInit) =>
        (init?.method ?? 'GET') === 'POST'
          ? Promise.resolve(
              new Response(
                JSON.stringify({
                  status: 400,
                  detail: 'A requisicao contem campos invalidos.',
                  codigo: 'validacao_falhou',
                }),
                {
                  status: 400,
                  headers: { 'Content-Type': 'application/problem+json' },
                },
              ),
            )
          : Promise.resolve(json({ itens: [INTEGRACAO], total: 1 })),
      ),
    );

    montar();
    await screen.findByRole('rowheader', { name: 'Checkout web' });

    await userEvent.type(screen.getByLabelText('Nome'), 'x');
    await userEvent.click(screen.getByRole('button', { name: /Criar e emitir chave/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent('invalidos');
  });
});
