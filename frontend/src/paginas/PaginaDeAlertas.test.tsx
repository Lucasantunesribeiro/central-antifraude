import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import { consultaDaFila, idadeDoAlerta, FILTROS_INICIAIS } from '../api/alertas';
import type { Alerta, PaginaDeAlertas as Pagina } from '../api/alertas';
import { PaginaDeAlertas } from './PaginaDeAlertas';

/**
 * O que esta tela promete ao analista: **saber o que pegar primeiro**.
 *
 * Por isso os testes olham para três coisas. Primeiro, a prioridade nunca
 * chega sozinha pela cor — o texto vai junto. Segundo, o caminho até o caso
 * concreto existe em toda linha: a transação está a um clique. Terceiro, e o
 * mais importante, a tela **não mente sobre o tamanho do trabalho**: o total
 * é o total do servidor, e quando um filtro esconde alertas ela diz que está
 * filtrando.
 */

const ALERTA_ALTO: Alerta = {
  id: '01a069e3-1111-7da4-9b84-7bfdb54799ae',
  transacaoId: '01a069e3-2222-7da4-9b84-7bfdb54799ae',
  avaliacaoId: '01a069e3-3333-7da4-9b84-7bfdb54799ae',
  decisao: 'Bloquear',
  score: 75,
  prioridade: 'Alta',
  status: 'Aberto',
  casoId: null,
  avaliadaEm: '2026-09-05T10:00:00Z',
  criadoEm: '2026-09-05T10:00:02Z',
  versaoDaPolitica: 1,
  principaisSinais: [
    { tipo: 'ValorAcimaDoHistorico', pontos: 30 },
    { tipo: 'DivergenciaGeografica', pontos: 25 },
    { tipo: 'NovoDispositivo', pontos: 20 },
  ],
  totalDeSinais: 3,
};

const ALERTA_MEDIO: Alerta = {
  ...ALERTA_ALTO,
  id: '01a069e3-4444-7da4-9b84-7bfdb54799ae',
  transacaoId: '01a069e3-5555-7da4-9b84-7bfdb54799ae',
  avaliacaoId: '01a069e3-6666-7da4-9b84-7bfdb54799ae',
  decisao: 'Revisar',
  score: 45,
  prioridade: 'Media',
  principaisSinais: [{ tipo: 'DivergenciaGeografica', pontos: 25 }],
  totalDeSinais: 1,
};

function pagina(itens: Alerta[], total = itens.length, numero = 1): Pagina {
  return {
    itens,
    pagina: numero,
    tamanho: 25,
    total,
    totalDePaginas: Math.ceil(total / 25),
  };
}

/**
 * Uma resposta nova a cada chamada.
 *
 * O corpo de um `Response` só pode ser lido uma vez. Reaproveitar a mesma
 * instância entre chamadas fez a suíte da Fase 1 sair com código 1 sem
 * reprovar teste nenhum — o defeito está registrado no ROADMAP 3.13.
 */
function montar(respostas: Pagina[]) {
  let chamada = 0;
  const urls: string[] = [];

  const buscar = vi.fn((url: string) => {
    urls.push(url);

    const corpo = respostas[Math.min(chamada, respostas.length - 1)];
    chamada += 1;

    return Promise.resolve(
      new Response(JSON.stringify(corpo), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
  });

  vi.stubGlobal('fetch', buscar);

  render(
    <QueryClientProvider client={criarClienteDeConsultas()}>
      <MemoryRouter initialEntries={['/alertas']}>
        <PaginaDeAlertas />
      </MemoryRouter>
    </QueryClientProvider>,
  );

  return urls;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('fila de alertas', () => {
  it('mostra prioridade, decisão, score e sinais de cada alerta', async () => {
    montar([pagina([ALERTA_ALTO, ALERTA_MEDIO])]);

    // As asserções olham DENTRO da tabela: a barra de filtros também tem as
    // palavras "Alta" e "Média", nas opções do seletor de prioridade.
    const tabela = await screen.findByRole('table');

    expect(within(tabela).getByText('Alta')).toBeInTheDocument();
    expect(within(tabela).getByText('Média')).toBeInTheDocument();

    // A decisão continua aparecendo: prioridade é a urgência operacional,
    // decisão é o que o motor recomendou. As duas não são a mesma coisa.
    expect(within(tabela).getByText('Bloquear')).toBeInTheDocument();
    expect(within(tabela).getByText('Revisar')).toBeInTheDocument();

    expect(within(tabela).getByText('75')).toBeInTheDocument();
    expect(within(tabela).getByText('45')).toBeInTheDocument();

    expect(within(tabela).getByText(/Valor acima do histórico/)).toBeInTheDocument();
    expect(within(tabela).getAllByText(/Divergência geográfica/)).toHaveLength(2);
  });

  it('leva de cada alerta para a transação que o originou', async () => {
    // A navegação do ROADMAP 6.6: da fila para a transação, e de lá para a
    // avaliação e os sinais completos.
    montar([pagina([ALERTA_ALTO])]);

    const ligacao = await screen.findByRole('link', { name: 'Abrir transação' });

    expect(ligacao).toHaveAttribute('href', `/transacoes/${ALERTA_ALTO.transacaoId}`);
  });

  it('avisa quando há mais sinais do que os exibidos', async () => {
    // Sem o aviso, três sinais numa avaliação de cinco fariam o analista
    // acreditar que viu tudo o que pesou.
    montar([pagina([{ ...ALERTA_ALTO, totalDeSinais: 5 }])]);

    expect(await screen.findByText('e mais 2')).toBeInTheDocument();
  });

  it('mostra o total do servidor, e não o tamanho da página', async () => {
    // Uma tela que dissesse "2 alertas" com 120 na fila mentiria sobre o
    // tamanho do trabalho que existe.
    montar([pagina([ALERTA_ALTO, ALERTA_MEDIO], 120)]);

    expect(await screen.findByText(/120 alerta\(s\)/)).toBeInTheDocument();
  });

  it('pede o filtro ao servidor em vez de esconder linhas na tela', async () => {
    const urls = montar([pagina([ALERTA_ALTO, ALERTA_MEDIO]), pagina([ALERTA_ALTO])]);

    await screen.findByRole('table');

    await userEvent.selectOptions(screen.getByLabelText('Prioridade'), 'Alta');

    await waitFor(() => expect(urls.length).toBeGreaterThan(1));

    expect(urls.at(-1)).toContain('prioridade=Alta');
    expect(urls.at(-1)).toContain('pagina=1');
  });

  it('volta para a primeira página ao mudar um filtro', async () => {
    // Manter a página 7 depois de filtrar mostraria uma tela vazia de um
    // resultado que tem uma página só.
    const urls = montar([pagina([ALERTA_ALTO], 120, 3)]);

    await screen.findByRole('table');

    await userEvent.selectOptions(screen.getByLabelText('Decisão'), 'Revisar');

    await waitFor(() => expect(urls.length).toBeGreaterThan(1));

    expect(urls.at(-1)).toContain('pagina=1');
  });

  it('diz que está filtrando quando a fila filtrada fica vazia', async () => {
    // "Nenhum alerta na fila" e "nenhum alerta com esses filtros" são
    // situações diferentes, e confundi-las faria alguém concluir que não há
    // trabalho quando só o filtro estava apertado.
    const urls = montar([pagina([ALERTA_ALTO]), pagina([])]);

    await screen.findByRole('table');

    await userEvent.selectOptions(screen.getByLabelText('Prioridade'), 'Media');

    expect(
      await screen.findByText('Nenhum alerta com esses filtros.'),
    ).toBeInTheDocument();
    expect(urls.at(-1)).toContain('prioridade=Media');
  });

  it('mostra a fila vazia sem sugerir que houve filtro', async () => {
    montar([pagina([])]);

    expect(await screen.findByText('Nenhum alerta na fila.')).toBeInTheDocument();
  });

  it('não oferece nenhuma ação sobre o alerta', async () => {
    // Agir sobre um alerta é ação de caso, e casos são a Fase 7. A tela não
    // pode oferecer um botão que o backend recusaria — nem esconder um botão
    // como se isso fosse autorização.
    montar([pagina([ALERTA_ALTO])]);

    const tabela = await screen.findByRole('table');

    expect(within(tabela).queryAllByRole('button')).toHaveLength(0);
  });
});

describe('consulta da fila', () => {
  it('omite os campos vazios em vez de mandá-los em branco', () => {
    // O backend recusa valor fora do vocabulário: mandar `decisao=`
    // transformaria "não filtrei" em erro.
    const consulta = consultaDaFila(FILTROS_INICIAIS);

    expect(consulta).not.toContain('decisao=');
    expect(consulta).not.toContain('prioridade=');
    expect(consulta).not.toContain('scoreMinimo=');
    expect(consulta).toContain('ordenarPor=criadoEm');
    expect(consulta).toContain('direcao=desc');
  });

  it('envia os filtros preenchidos', () => {
    const consulta = consultaDaFila({
      ...FILTROS_INICIAIS,
      decisao: 'Bloquear',
      prioridade: 'Alta',
      scoreMinimo: ' 70 ',
      pagina: 2,
    });

    expect(consulta).toContain('decisao=Bloquear');
    expect(consulta).toContain('prioridade=Alta');
    expect(consulta).toContain('scoreMinimo=70');
    expect(consulta).toContain('pagina=2');
  });
});

describe('idade do alerta', () => {
  const agora = new Date('2026-09-05T12:00:00Z');

  it.each([
    ['2026-09-05T11:59:30Z', '30s'],
    ['2026-09-05T11:30:00Z', '30min'],
    ['2026-09-05T09:00:00Z', '3h'],
    ['2026-09-03T12:00:00Z', '2d'],
  ])('resume %s como %s', (criadoEm, esperado) => {
    expect(idadeDoAlerta(criadoEm, agora)).toBe(esperado);
  });

  it('nunca mostra idade negativa', () => {
    // O relógio do navegador pode estar atrás do servidor. "-4s de espera" na
    // fila seria absurdo na tela.
    expect(idadeDoAlerta('2026-09-05T12:00:04Z', agora)).toBe('0s');
  });
});
