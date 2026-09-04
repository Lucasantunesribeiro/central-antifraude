import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import { ProvedorDeSessao } from '../sessao/ProvedorDeSessao';
import { RotaProtegida } from './RotaProtegida';

/**
 * A protecao de rota e conveniencia de navegacao, nao seguranca — quem recusa
 * de fato e a API. O que estes testes garantem e que o usuario nao ve telas
 * que nao pode usar, e que um F5 nao o expulsa antes de a sessao voltar.
 */

function respostaDeSessao(perfil: string) {
  return new Response(
    JSON.stringify({
      accessToken: 'token-de-teste',
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
  return new Response(JSON.stringify({ status: 401, codigo: 'sessao_invalida' }), {
    status: 401,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

function montar(perfis?: readonly ('Administrador' | 'Auditor')[]) {
  const cliente = criarClienteDeConsultas();

  return render(
    <QueryClientProvider client={cliente}>
      <MemoryRouter initialEntries={['/protegida']}>
        <ProvedorDeSessao>
          <Routes>
            <Route path="/entrar" element={<p>tela de login</p>} />
            <Route path="/painel" element={<p>painel</p>} />
            <Route element={<RotaProtegida perfis={perfis} />}>
              <Route path="/protegida" element={<p>area protegida</p>} />
            </Route>
          </Routes>
        </ProvedorDeSessao>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RotaProtegida', () => {
  it('espera a restauracao da sessao antes de decidir', () => {
    // Sem este estado, um F5 jogaria o usuario para a tela de login antes de
    // o cookie ser conferido - o bug de sessao que a Fase 1 precisa evitar.
    vi.stubGlobal('fetch', vi.fn().mockReturnValue(new Promise(() => {})));

    montar();

    expect(screen.getByRole('status')).toHaveTextContent('Restaurando sessao...');
    expect(screen.queryByText('tela de login')).not.toBeInTheDocument();
    expect(screen.queryByText('area protegida')).not.toBeInTheDocument();
  });

  it('manda para o login quando nao ha sessao', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(semSessao()));

    montar();

    expect(await screen.findByText('tela de login')).toBeInTheDocument();
    expect(screen.queryByText('area protegida')).not.toBeInTheDocument();
  });

  it('libera a rota quando a sessao foi restaurada pelo cookie', async () => {
    // Este e o caminho do F5: o frontend nao tem token, pergunta ao servidor
    // e recebe a sessao de volta.
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(respostaDeSessao('Auditor')));

    montar();

    expect(await screen.findByText('area protegida')).toBeInTheDocument();
  });

  it('desvia da rota quando o perfil nao alcanca', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(respostaDeSessao('Auditor')));

    montar(['Administrador']);

    expect(await screen.findByText('painel')).toBeInTheDocument();
    expect(screen.queryByText('area protegida')).not.toBeInTheDocument();
  });

  it('libera a rota quando o perfil alcanca', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(respostaDeSessao('Administrador')),
    );

    montar(['Administrador']);

    expect(await screen.findByText('area protegida')).toBeInTheDocument();
  });
});
