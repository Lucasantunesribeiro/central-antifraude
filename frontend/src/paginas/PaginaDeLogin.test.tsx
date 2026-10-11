import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { ContextoDeSessao, type Sessao } from '../sessao/contextoDeSessao';
import { PaginaDeLogin } from './PaginaDeLogin';

/**
 * A tela de login.
 *
 * Duas promessas: o acesso de demonstração vem em primeiro e sem senha, e o
 * formulário de conta chama `entrar` com o que a pessoa digitou.
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
  render(
    <MemoryRouter initialEntries={['/entrar']}>
      <ContextoDeSessao.Provider value={sessao}>
        <PaginaDeLogin />
      </ContextoDeSessao.Provider>
    </MemoryRouter>,
  );
}

describe('página de login', () => {
  it('oferece o acesso de demonstração em destaque, sem senha', async () => {
    const entrarComoDemo = vi.fn(async () => {});
    montar(sessaoFake({ entrarComoDemo }));

    await userEvent.click(
      screen.getByRole('button', { name: /explorar a demonstra/i }),
    );

    expect(entrarComoDemo).toHaveBeenCalledOnce();
  });

  it('entra com a conta usando o que foi digitado', async () => {
    const entrar = vi.fn(async () => {});
    montar(sessaoFake({ entrar }));

    await userEvent.type(screen.getByLabelText(/e-mail/i), 'analista@demo.local');
    await userEvent.type(screen.getByLabelText(/senha/i), 'segredo-de-teste');
    await userEvent.click(screen.getByRole('button', { name: /^entrar$/i }));

    expect(entrar).toHaveBeenCalledWith('analista@demo.local', 'segredo-de-teste');
  });
});
