import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import type { BacktestDetalhado } from '../api/backtests';
import { PaginaDoBacktest } from './PaginaDoBacktest';

/**
 * O resultado de uma simulação.
 *
 * O que estes testes protegem é a honestidade da leitura:
 *
 * 1. **o candidato congelado aparece por inteiro** — o Supervisor precisa ver
 *    que o resultado fala do rascunho de então, e não do que ele editou
 *    depois;
 * 2. **cada contagem por veredito traz o próprio denominador**, e "sem
 *    investigação" é uma linha distinta de "inconclusiva";
 * 3. **cancelar carrega a versão lida**, que é o que impede cancelar sobre
 *    uma tela velha — e o que faz o worker perder a corrida.
 */

const ID = '01a069e3-5555-7da4-9b84-7bfdb54799ae';

const CONCLUIDO: BacktestDetalhado = {
  execucao: {
    id: ID,
    descricao: 'Rascunho de "Velocidade por cliente"',
    regraCandidataId: '01a069e3-2222-7da4-9b84-7bfdb54799ae',
    status: 'Concluida',
    inicio: '2026-08-07T00:00:00Z',
    fim: '2026-09-06T00:00:00Z',
    numeroDaVersaoDePerfilVigente: 3,
    solicitadaPor: 'Supervisora de Teste',
    solicitadaEm: '2026-09-06T12:00:00Z',
    concluidaEm: '2026-09-06T12:00:05Z',
    totalAnalisado: 120,
    totalDeMudancas: 14,
    mensagemDeErro: null,
    versao: 3,
  },
  candidato: {
    limiarDeRevisao: 40,
    limiarDeBloqueio: 70,
    regras: [
      {
        regraId: '01a069e3-2222-7da4-9b84-7bfdb54799ae',
        nome: 'Velocidade por cliente',
        tipo: 'VelocidadePorCliente',
        configuracao: 'mais de 2 tentativa(s) em 30 minuto(s)',
        pontos: 45,
        origem: 'Rascunho',
      },
      {
        regraId: '01a069e3-3333-7da4-9b84-7bfdb54799ae',
        nome: 'Dispositivo novo',
        tipo: 'NovoDispositivo',
        configuracao: 'dispositivo nao visto antes',
        pontos: 20,
        origem: 'Publicada',
      },
    ],
  },
  resultado: {
    totalAnalisado: 120,
    totalQueAcionaria: 44,
    totalDeMudancas: 14,
    vigente: { permitir: 100, revisar: 15, bloquear: 5 },
    candidato: { permitir: 90, revisar: 22, bloquear: 8 },
    mudancas: [
      { de: 'Permitir', para: 'Revisar', quantidade: 11 },
      { de: 'Revisar', para: 'Bloquear', quantidade: 3 },
    ],
    porVeredito: [
      {
        veredito: 'FraudeConfirmada',
        total: 3,
        vigente: { permitir: 0, revisar: 2, bloquear: 1 },
        candidato: { permitir: 0, revisar: 0, bloquear: 3 },
      },
      {
        veredito: null,
        total: 117,
        vigente: { permitir: 100, revisar: 13, bloquear: 4 },
        candidato: { permitir: 90, revisar: 22, bloquear: 5 },
      },
    ],
    faixasDeScore: [
      { de: 0, ate: 19, vigente: 100, candidato: 90 },
      { de: 20, ate: 39, vigente: 0, candidato: 0 },
      { de: 40, ate: 59, vigente: 15, candidato: 22 },
      { de: 60, ate: 79, vigente: 3, candidato: 5 },
      { de: 80, ate: 100, vigente: 2, candidato: 3 },
    ],
  },
};

function json(corpo: unknown, status = 200) {
  return new Response(JSON.stringify(corpo), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

interface Chamada {
  url: string;
  metodo: string;
  corpo: unknown;
}

function montar(detalhe: BacktestDetalhado) {
  const chamadas: Chamada[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, opcoes?: RequestInit) => {
      chamadas.push({
        url,
        metodo: opcoes?.method ?? 'GET',
        corpo: opcoes?.body ? JSON.parse(String(opcoes.body)) : null,
      });

      return Promise.resolve(json(detalhe));
    }),
  );

  render(
    <QueryClientProvider client={criarClienteDeConsultas()}>
      <MemoryRouter initialEntries={[`/backtests/${ID}`]}>
        <Routes>
          <Route path="/backtests/:id" element={<PaginaDoBacktest />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );

  return chamadas;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('detalhe do backtest', () => {
  it('mostra o candidato congelado, marcando o que é a mudança', async () => {
    montar(CONCLUIDO);

    expect(
      await screen.findByRole('heading', { name: 'Perfil candidato' }),
    ).toBeInTheDocument();

    const tabelas = screen.getAllByRole('table');
    const candidato = tabelas[0]!;

    expect(within(candidato).getByText('Rascunho (a mudança)')).toBeInTheDocument();
    expect(within(candidato).getByText('Em vigor')).toBeInTheDocument();
    expect(
      within(candidato).getByText('mais de 2 tentativa(s) em 30 minuto(s)'),
    ).toBeInTheDocument();
  });

  it('diz quantas decisões mudariam e em que direção', async () => {
    montar(CONCLUIDO);

    await screen.findByRole('heading', { name: 'O que mudaria' });

    expect(screen.getByText(/120 transação\(ões\) analisada\(s\)/)).toBeInTheDocument();

    // As duas transições apertam a decisão — e a coluna precisa dizer isso
    // linha a linha, porque "mudou" sem direção não ajuda a decidir.
    expect(screen.getAllByText('Mais rígido')).toHaveLength(2);
    expect(screen.queryByText('Mais permissivo')).toBeNull();
  });

  it('separa ausência de investigação de veredito', async () => {
    // Contagem bruta com denominador visível — nunca uma taxa de acerto. A
    // maioria das transações nunca foi investigada.
    montar(CONCLUIDO);

    await screen.findByRole('heading', {
      name: 'Cruzamento com a investigação humana',
    });

    expect(screen.getByText('Fraude confirmada')).toBeInTheDocument();
    expect(screen.getByText('Sem investigação')).toBeInTheDocument();
    expect(screen.queryByText(/precisão|recall/i)).toBeNull();
  });

  it('envia a versão que leu ao cancelar', async () => {
    const chamadas = montar({
      ...CONCLUIDO,
      execucao: { ...CONCLUIDO.execucao, status: 'Pendente' },
      resultado: null,
    });

    await userEvent.click(
      await screen.findByRole('button', { name: 'Cancelar execução' }),
    );

    await waitFor(() =>
      expect(chamadas.some((c) => c.url.includes('/cancelamento'))).toBe(true),
    );

    const acao = chamadas.find((c) => c.url.includes('/cancelamento'))!;

    expect(acao.metodo).toBe('POST');
    expect(acao.corpo).toEqual({ versao: 3 });
  });

  it('não oferece cancelar o que já terminou', async () => {
    montar(CONCLUIDO);

    await screen.findByRole('heading', { name: 'Perfil candidato' });

    expect(screen.queryByRole('button', { name: 'Cancelar execução' })).toBeNull();
  });

  it('avisa que ainda não terminou, em vez de mostrar resultado vazio', async () => {
    montar({
      ...CONCLUIDO,
      execucao: { ...CONCLUIDO.execucao, status: 'Executando' },
      resultado: null,
    });

    expect(
      await screen.findByText(/A execução ainda não terminou/),
    ).toBeInTheDocument();
  });

  it('mostra o motivo quando a execução falhou', async () => {
    // Um "Falhou" sem motivo obrigaria o Supervisor a adivinhar se o problema
    // foi dele ou do sistema.
    montar({
      ...CONCLUIDO,
      execucao: {
        ...CONCLUIDO.execucao,
        status: 'Falhou',
        mensagemDeErro: 'A janela passou a ter 9000 transacoes e o limite e 5000.',
        totalAnalisado: null,
        totalDeMudancas: null,
      },
      resultado: null,
    });

    expect(
      await screen.findByText(/A janela passou a ter 9000 transacoes/),
    ).toBeInTheDocument();
  });
});
