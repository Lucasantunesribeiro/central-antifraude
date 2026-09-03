import { QueryClient } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { criarClienteDeConsultas } from './api/clienteDeConsultas';
import { ErroDaApi } from './api/erros';

/**
 * Smoke test do shell: a aplicacao monta, chama a API e passa pelos tres
 * estados de verdade - carregando, erro e sucesso.
 */

function clienteSilencioso(): QueryClient {
  const cliente = criarClienteDeConsultas();
  cliente.setDefaultOptions({
    queries: { ...cliente.getDefaultOptions().queries, retry: false },
  });
  return cliente;
}

function respostaDeSaude() {
  return new Response(
    JSON.stringify({
      estado: 'Healthy',
      duracaoEmMs: 12,
      componentes: { postgresql: 'Healthy' },
    }),
    { status: 200, headers: { 'Content-Type': 'application/json' } },
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('App', () => {
  it('monta o shell com a marca e a navegacao', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(respostaDeSaude()));

    render(<App cliente={clienteSilencioso()} />);

    expect(
      screen.getByRole('heading', { name: 'Central Antifraude', level: 1 }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('navigation', { name: 'Navegacao principal' }),
    ).toBeInTheDocument();

    // Sem sessao, a Fase 0 nao mostra link para area autenticada.
    expect(screen.queryByRole('link', { name: 'Console' })).not.toBeInTheDocument();
  });

  it('mostra o estado de carregamento antes da resposta da API', () => {
    vi.stubGlobal('fetch', vi.fn().mockReturnValue(new Promise(() => {})));

    render(<App cliente={clienteSilencioso()} />);

    expect(screen.getByRole('status')).toHaveTextContent('Consultando a API...');
  });

  it('mostra o estado do banco quando a API responde', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(respostaDeSaude()));

    render(<App cliente={clienteSilencioso()} />);

    expect(
      await screen.findByRole('rowheader', { name: 'postgresql' }),
    ).toBeInTheDocument();
    expect(screen.getAllByText('Operacional').length).toBeGreaterThanOrEqual(2);
  });

  it('mostra o erro e o codigo de suporte quando a API falha', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ status: 503, detail: 'Indisponivel.' }), {
          status: 503,
          headers: {
            'Content-Type': 'application/problem+json',
            'X-Correlation-Id': 'suporte-0001',
          },
        }),
      ),
    );

    render(<App cliente={clienteSilencioso()} />);

    const alerta = await screen.findByRole('alert');
    expect(alerta).toHaveTextContent('servidor nao conseguiu concluir');
    // O identificador precisa estar visivel: e o que o analista informa ao
    // suporte para achar a requisicao no log.
    expect(alerta).toHaveTextContent('suporte-0001');
  });
});

describe('politica de repeticao', () => {
  it('nao repete erro que nao adianta repetir', () => {
    const cliente = criarClienteDeConsultas();
    const repetir = cliente.getDefaultOptions().queries?.retry;

    expect(typeof repetir).toBe('function');

    const decidir = repetir as (tentativas: number, erro: Error) => boolean;

    expect(decidir(0, new ErroDaApi('invalido', 'validacao'))).toBe(false);
    expect(decidir(0, new ErroDaApi('sem rede', 'rede'))).toBe(true);
    expect(decidir(5, new ErroDaApi('sem rede', 'rede'))).toBe(false);
  });
});
