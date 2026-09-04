import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import { CABECALHO_DE_CORRELACAO, requisitar } from '../api/clienteHttp';
import { ProvedorDeSessao } from './ProvedorDeSessao';
import { useSessao } from './contextoDeSessao';

/**
 * O provedor de sessao guarda a decisao mais sensivel do frontend: onde o
 * token vive. Estes testes existem para que ela nao mude por descuido.
 */

function sessaoValida(perfil = 'Administrador', token = 'token-de-teste') {
  return new Response(
    JSON.stringify({
      accessToken: token,
      expiraEm: new Date(Date.now() + 15 * 60_000).toISOString(),
      usuario: {
        id: '01a0699c-68c5-7e77-b49d-62a43bee10a8',
        email: 'pessoa@teste.local',
        nomeCompleto: 'Pessoa de Teste',
        perfil,
        organizacaoId: '01a0699c-676d-78ca-a854-bfc4a78c1fa6',
      },
    }),
    { status: 200, headers: { 'Content-Type': 'application/json' } },
  );
}

function semSessao() {
  return new Response(JSON.stringify({ status: 401 }), {
    status: 401,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

function Sonda() {
  const sessao = useSessao();

  return (
    <div>
      <p data-testid="estado">
        {sessao.carregando
          ? 'carregando'
          : sessao.autenticado
            ? 'autenticado'
            : 'anonimo'}
      </p>
      <p data-testid="nome">{sessao.usuario?.nomeCompleto ?? '-'}</p>
      <button
        type="button"
        onClick={() => void sessao.entrar('a@b.com', 'senha-longa-ok')}
      >
        entrar
      </button>
      <button type="button" onClick={() => void sessao.sair()}>
        sair
      </button>
    </div>
  );
}

function montar() {
  return render(
    <QueryClientProvider client={criarClienteDeConsultas()}>
      <MemoryRouter>
        <ProvedorDeSessao>
          <Sonda />
        </ProvedorDeSessao>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
  sessionStorage.clear();
});

describe('ProvedorDeSessao', () => {
  it('tenta restaurar a sessao pelo cookie ao montar', async () => {
    const espiao = vi.fn().mockResolvedValue(sessaoValida());
    vi.stubGlobal('fetch', espiao);

    montar();

    await waitFor(() =>
      expect(screen.getByTestId('estado')).toHaveTextContent('autenticado'),
    );

    expect(espiao.mock.calls[0]?.[0]).toContain('/api/auth/refresh');
    expect(screen.getByTestId('nome')).toHaveTextContent('Pessoa de Teste');
  });

  it('a requisicao de refresh envia o cookie da sessao', async () => {
    // credentials: 'include' e o que faz o navegador anexar o cookie
    // HttpOnly. Sem isso, a restauracao de sessao nunca funcionaria.
    const espiao = vi.fn().mockResolvedValue(sessaoValida());
    vi.stubGlobal('fetch', espiao);

    montar();

    await waitFor(() => expect(espiao).toHaveBeenCalled());

    const opcoes = espiao.mock.calls[0]?.[1] as RequestInit;
    expect(opcoes.credentials).toBe('include');
  });

  it('fica anonimo quando o cookie nao vale mais', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(semSessao()));

    montar();

    await waitFor(() =>
      expect(screen.getByTestId('estado')).toHaveTextContent('anonimo'),
    );
  });

  it('o token de acesso nunca e gravado no navegador', async () => {
    // A decisao central: token so em memoria. Um XSS le localStorage e
    // sessionStorage; nao le uma variavel de modulo.
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(sessaoValida('Auditor', 'segredo-do-token')),
    );

    montar();

    await waitFor(() =>
      expect(screen.getByTestId('estado')).toHaveTextContent('autenticado'),
    );

    expect(JSON.stringify(localStorage)).not.toContain('segredo-do-token');
    expect(JSON.stringify(sessionStorage)).not.toContain('segredo-do-token');
    expect(document.cookie).not.toContain('segredo-do-token');
  });

  it('depois do login as chamadas seguintes levam o token', async () => {
    const espiao = vi
      .fn()
      .mockResolvedValueOnce(semSessao())
      .mockResolvedValueOnce(sessaoValida('Administrador', 'token-apos-login'))
      .mockResolvedValue(
        new Response('{}', {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    vi.stubGlobal('fetch', espiao);

    montar();
    await waitFor(() =>
      expect(screen.getByTestId('estado')).toHaveTextContent('anonimo'),
    );

    await userEvent.click(screen.getByRole('button', { name: 'entrar' }));
    await waitFor(() =>
      expect(screen.getByTestId('estado')).toHaveTextContent('autenticado'),
    );

    await requisitar('/api/usuarios');

    const ultima = espiao.mock.calls.at(-1);
    expect(ultima).toBeDefined();

    const cabecalhos = (ultima![1] as RequestInit).headers as Record<string, string>;

    expect(cabecalhos.Authorization).toBe('Bearer token-apos-login');
    expect(cabecalhos[CABECALHO_DE_CORRELACAO]).toBeTruthy();
  });

  it('o login nao envia o token antigo no cabecalho', async () => {
    const espiao = vi.fn().mockResolvedValue(sessaoValida());
    vi.stubGlobal('fetch', espiao);

    montar();
    await waitFor(() =>
      expect(screen.getByTestId('estado')).toHaveTextContent('autenticado'),
    );

    await userEvent.click(screen.getByRole('button', { name: 'entrar' }));

    const chamadaDeLogin = espiao.mock.calls.find((c) =>
      String(c[0]).includes('/api/auth/login'),
    );
    expect(chamadaDeLogin).toBeDefined();

    const cabecalhos = (chamadaDeLogin![1] as RequestInit).headers as Record<
      string,
      string
    >;

    expect(cabecalhos.Authorization).toBeUndefined();
  });

  it('sair encerra a sessao mesmo se o servidor falhar', async () => {
    // Deixar o usuario preso numa tela autenticada porque o logout falhou
    // seria pior do que encerrar de um lado so.
    const espiao = vi
      .fn()
      .mockResolvedValueOnce(sessaoValida())
      .mockRejectedValue(new TypeError('Failed to fetch'));
    vi.stubGlobal('fetch', espiao);

    montar();
    await waitFor(() =>
      expect(screen.getByTestId('estado')).toHaveTextContent('autenticado'),
    );

    await userEvent.click(screen.getByRole('button', { name: 'sair' }));

    await waitFor(() =>
      expect(screen.getByTestId('estado')).toHaveTextContent('anonimo'),
    );
  });

  it('sair chama o endpoint de logout do servidor', async () => {
    // Encerrar so no cliente deixaria o refresh token vivo no banco ate
    // expirar - e ele e o que da acesso.
    const espiao = vi
      .fn()
      .mockResolvedValueOnce(sessaoValida())
      .mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', espiao);

    montar();
    await waitFor(() =>
      expect(screen.getByTestId('estado')).toHaveTextContent('autenticado'),
    );

    await userEvent.click(screen.getByRole('button', { name: 'sair' }));

    await waitFor(() =>
      expect(
        espiao.mock.calls.some((c) => String(c[0]).includes('/api/auth/logout')),
      ).toBe(true),
    );
  });
});
