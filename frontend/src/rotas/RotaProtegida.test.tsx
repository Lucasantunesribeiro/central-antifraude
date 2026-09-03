import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { RotaProtegida } from './RotaProtegida';
import * as sessao from '../sessao/useSessao';

describe('RotaProtegida', () => {
  it('redireciona para a rota publica quando nao ha sessao', () => {
    render(
      <MemoryRouter initialEntries={['/console']}>
        <Routes>
          <Route path="/" element={<p>rota publica</p>} />
          <Route element={<RotaProtegida />}>
            <Route path="/console" element={<p>area autenticada</p>} />
          </Route>
        </Routes>
      </MemoryRouter>,
    );

    expect(screen.getByText('rota publica')).toBeInTheDocument();
    expect(screen.queryByText('area autenticada')).not.toBeInTheDocument();
  });

  it('libera a rota quando a sessao esta autenticada', () => {
    vi.spyOn(sessao, 'useSessao').mockReturnValue({
      autenticado: true,
      carregando: false,
      nome: 'Analista de teste',
      perfil: 'AnalistaDeFraude',
    });

    render(
      <MemoryRouter initialEntries={['/console']}>
        <Routes>
          <Route path="/" element={<p>rota publica</p>} />
          <Route element={<RotaProtegida />}>
            <Route path="/console" element={<p>area autenticada</p>} />
          </Route>
        </Routes>
      </MemoryRouter>,
    );

    expect(screen.getByText('area autenticada')).toBeInTheDocument();
  });

  it('espera antes de decidir enquanto a sessao carrega', () => {
    // Sem este estado, um F5 jogaria o usuario para fora antes de a sessao
    // ser restaurada - o bug de sessao que a Fase 1 precisa evitar.
    vi.spyOn(sessao, 'useSessao').mockReturnValue({
      autenticado: false,
      carregando: true,
    });

    render(
      <MemoryRouter initialEntries={['/console']}>
        <Routes>
          <Route path="/" element={<p>rota publica</p>} />
          <Route element={<RotaProtegida />}>
            <Route path="/console" element={<p>area autenticada</p>} />
          </Route>
        </Routes>
      </MemoryRouter>,
    );

    expect(screen.getByRole('status')).toHaveTextContent('Restaurando sessao...');
    expect(screen.queryByText('rota publica')).not.toBeInTheDocument();
  });
});
