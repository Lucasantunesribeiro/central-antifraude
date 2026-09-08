import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import { pico, proporcao, quantidadeDe, rotularOperacao } from '../api/operacao';
import { PaginaDoPainel } from './PaginaDoPainel';

/**
 * O painel operacional.
 *
 * Três promessas são verificadas aqui:
 *
 * 1. **recebidas e avaliadas são números distintos** — igualá-los esconderia
 *    o evento atrasado, que é justamente o comportamento que o produto trata;
 * 2. **as três decisões sempre aparecem**, mesmo zeradas: uma faixa que some
 *    faria o analista achar que ela deixou de existir;
 * 3. **as métricas de regra não viram taxa de acerto** — são contagens com o
 *    denominador à vista, e a tela diz por quê.
 */

const PAINEL = {
  dias: 7,
  inicio: '2026-08-31T12:00:00Z',
  fim: '2026-09-07T12:00:00Z',
  transacoesRecebidas: 12,
  transacoesAvaliadas: 10,
  decisoes: [
    { chave: 'Permitir', quantidade: 7 },
    { chave: 'Revisar', quantidade: 3 },
    { chave: 'Bloquear', quantidade: 0 },
  ],
  tendencia: [
    { dia: '2026-09-05', permitir: 4, revisar: 1, bloquear: 0, total: 5 },
    { dia: '2026-09-06', permitir: 0, revisar: 0, bloquear: 0, total: 0 },
    { dia: '2026-09-07', permitir: 3, revisar: 2, bloquear: 0, total: 5 },
  ],
  sinaisMaisFrequentes: [
    { tipo: 'VelocidadePorCliente', acionamentos: 9 },
    { tipo: 'NovoDispositivo', acionamentos: 4 },
  ],
  alertasAbertos: 3,
  casos: [
    { chave: 'Novo', quantidade: 1 },
    { chave: 'EmAnalise', quantidade: 2 },
    { chave: 'Resolvido', quantidade: 5 },
  ],
  casosAntigos: 1,
  diasParaCasoAntigo: 3,
  eventosPendentes: 0,
};

const METRICAS = [
  {
    regraId: '01a069e3-2222-7da4-9b84-7bfdb54799ae',
    nome: 'Velocidade por cliente',
    tipo: 'VelocidadePorCliente',
    acionamentos: 9,
    fraudeConfirmada: 2,
    legitima: 1,
    inconclusiva: 0,
    semResultadoConhecido: 6,
  },
];

function json(corpo: unknown, status = 200) {
  return new Response(JSON.stringify(corpo), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function montar(painel: unknown = PAINEL) {
  const urls: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) => {
      urls.push(url);

      if (url.includes('/api/painel/regras')) {
        return Promise.resolve(json(METRICAS));
      }

      return Promise.resolve(json(painel));
    }),
  );

  render(
    <QueryClientProvider client={criarClienteDeConsultas()}>
      <MemoryRouter initialEntries={['/painel']}>
        <PaginaDoPainel />
      </MemoryRouter>
    </QueryClientProvider>,
  );

  return urls;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('painel operacional', () => {
  it('separa o que chegou do que foi decidido', async () => {
    // Uma transação atrasada chega hoje sobre um fato de ontem. Igualar os
    // dois números esconderia isso.
    montar();

    // Os dois numeros vivem em indicadores SEPARADOS, e o teste procura cada
    // um dentro do seu: um `getByText('12')` solto passaria mesmo que os dois
    // estivessem na mesma caixa, que e exatamente o que nao pode acontecer.
    const recebidas = (await screen.findByText('Recebidas')).closest(
      '.indicador',
    ) as HTMLElement;
    const avaliadas = screen
      .getByText('Avaliadas')
      .closest('.indicador') as HTMLElement;

    expect(within(recebidas).getByText('12')).toBeInTheDocument();
    expect(within(avaliadas).getByText('10')).toBeInTheDocument();
    expect(recebidas).not.toBe(avaliadas);

    expect(screen.getByText(/caminho assíncrono está em dia/)).toBeInTheDocument();
  });

  it('avisa quando há evento esperando publicação', async () => {
    // É a medida direta de fila parada: alertas dessas transações ainda não
    // foram criados, e o analista descobriria pelo silêncio.
    montar({ ...PAINEL, eventosPendentes: 4 });

    expect(
      await screen.findByText(/alertas destas transações ainda não foram criados/),
    ).toBeInTheDocument();
  });

  it('mostra as três decisões, inclusive a que ficou em zero', async () => {
    montar();

    const cartao = (await screen.findByText('Decisões no período')).closest(
      '.cartao',
    ) as HTMLElement;
    const linhas = within(cartao).getAllByRole('row').slice(1);

    expect(linhas).toHaveLength(3);
    expect(linhas[2]).toHaveTextContent('Bloquear');
    expect(linhas[2]).toHaveTextContent('0');
  });

  it('mostra a tendência sem pular dias', async () => {
    // Uma linha do tempo que pula dias faz um fim de semana parado parecer um
    // pico na segunda.
    montar();

    const cartao = (await screen.findByText('Tendência')).closest(
      '.cartao',
    ) as HTMLElement;
    const linhas = within(cartao).getAllByRole('row').slice(1);

    expect(linhas).toHaveLength(3);
    expect(linhas[1]).toHaveTextContent('2026-09-06');
  });

  it('mostra as métricas de regra sem chamar nada de taxa de acerto', async () => {
    montar();

    const cartao = (await screen.findByText('Regras no período')).closest(
      '.cartao',
    ) as HTMLElement;

    expect(within(cartao).getByText('Velocidade por cliente')).toBeInTheDocument();
    expect(within(cartao).getByText(/não são amostra aleatória/)).toBeInTheDocument();

    // A ausência que importa não é a da palavra — é a do NÚMERO. Nenhuma
    // porcentagem aparece no cartão de regras: só contagens brutas, cada uma
    // com o denominador ao lado.
    const celulas = within(cartao)
      .getAllByRole('cell')
      .map((c) => c.textContent ?? '');

    expect(celulas.some((texto) => texto.includes('%'))).toBe(false);
    expect(celulas).toContain('9');
    expect(celulas).toContain('6');
  });

  it('troca o período e pergunta de novo', async () => {
    const urls = montar();

    await screen.findByText('Recebidas');

    await userEvent.click(screen.getByRole('button', { name: '30 dias' }));

    await waitFor(() => expect(urls.some((u) => u.includes('dias=30'))).toBe(true));

    // As métricas de regra acompanham o período: dois números do mesmo painel
    // que falassem de janelas diferentes não poderiam ser lidos juntos.
    expect(urls.some((u) => u.includes('/api/painel/regras?dias=30'))).toBe(true);
  });
});

describe('cálculos do painel', () => {
  it('encontra a contagem de uma decisão', () => {
    expect(quantidadeDe(PAINEL.decisoes, 'Revisar')).toBe(3);
    expect(quantidadeDe(PAINEL.decisoes, 'Inexistente')).toBe(0);
  });

  it('não divide por zero na proporção', () => {
    expect(proporcao(3, 10)).toBe(30);
    expect(proporcao(0, 0)).toBe(0);
  });

  it('acha o pico da série para dimensionar as barras', () => {
    expect(pico(PAINEL.tendencia)).toBe(5);
    expect(pico([])).toBe(0);
  });

  it('quebra o nome da operação em palavras', () => {
    // Um dicionário fixo ficaria desatualizado em silêncio a cada fase nova, e
    // a tela mostraria a operação recém-criada como um nome cru.
    expect(rotularOperacao('VersaoDeRegraPublicada')).toBe('Versao de regra publicada');
    expect(rotularOperacao('LoginBemSucedido')).toBe('Login bem sucedido');
  });
});
