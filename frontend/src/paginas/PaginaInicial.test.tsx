import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import { ContextoDeSessao, type Sessao } from '../sessao/contextoDeSessao';
import { PaginaInicial } from './PaginaInicial';

/**
 * A entrada do produto.
 *
 * O que importa aqui não é o texto, é a ação: um visitante sem conta precisa
 * conseguir entrar na demonstração num clique, e um visitante já autenticado
 * precisa ser levado ao painel em vez de reentrar.
 */

function sessaoFake(parcial: Partial<Sessao> = {}): Sessao {
  return {
    usuario: null,
    carregando: false,
    autenticado: false,
    entrar: vi.fn(async () => {}),
    entrarComoDemo: vi.fn(async () => {}),
    sair: vi.fn(async () => {}),
    ...parcial,
  };
}

function montar(sessao: Sessao) {
  // O health check vive num <details> recolhido, mas o hook roda mesmo assim.
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify({ estado: 'Healthy', componentes: {} }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );

  render(
    <QueryClientProvider client={criarClienteDeConsultas()}>
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route
            path="/"
            element={
              <ContextoDeSessao.Provider value={sessao}>
                <PaginaInicial />
              </ContextoDeSessao.Provider>
            }
          />
          <Route path="/painel" element={<p>painel aberto</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('página inicial', () => {
  it('leva o visitante à demonstração num clique e abre o painel', async () => {
    const entrarComoDemo = vi.fn(async () => {});
    montar(sessaoFake({ entrarComoDemo }));

    await userEvent.click(
      screen.getByRole('button', { name: /explorar a demonstração/i }),
    );

    expect(entrarComoDemo).toHaveBeenCalledOnce();
    expect(await screen.findByText('painel aberto')).toBeTruthy();
  });

  it('um visitante já autenticado é levado ao painel, não ao login', () => {
    montar(sessaoFake({ autenticado: true }));

    expect(screen.getByRole('link', { name: /ir ao painel/i })).toBeTruthy();
    expect(
      screen.queryByRole('button', { name: /explorar a demonstração/i }),
    ).toBeNull();
  });

  it('o diagnóstico de infraestrutura continua acessível, mas recolhido', () => {
    montar(sessaoFake());

    // Continua na página (CA-03 pede mantê-lo), dentro de um <details>.
    expect(screen.getByText(/diagnóstico técnico da infraestrutura/i)).toBeTruthy();
  });
});
