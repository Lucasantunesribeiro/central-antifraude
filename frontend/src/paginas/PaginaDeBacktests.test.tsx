import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import {
  direcaoDaMudanca,
  estaEmAndamento,
  podeCancelar,
  rotularVeredito,
  variacao,
} from '../api/backtests';
import { PaginaDeBacktests } from './PaginaDeBacktests';

/**
 * A tela de backtests.
 *
 * Três promessas são verificadas aqui:
 *
 * 1. **só rascunho é simulável** — oferecer uma regra sem alteração pendente
 *    faria a pessoa descobrir o `409` depois de clicar;
 * 2. **a tela não monta configuração nenhuma** — ela manda o identificador da
 *    regra, e o servidor monta o candidato a partir do que já está gravado;
 * 3. **a lista para de reperguntar quando nada está em andamento**, senão
 *    consultaria o servidor para sempre depois da última execução.
 */

const PERFIL = {
  versaoId: '01a069e3-1111-7da4-9b84-7bfdb54799ae',
  numero: 3,
  limiarDeRevisao: 40,
  limiarDeBloqueio: 70,
  publicadaEm: '2026-09-06T10:00:00Z',
  regras: [],
};

const COM_RASCUNHO = {
  id: '01a069e3-2222-7da4-9b84-7bfdb54799ae',
  tipo: 'VelocidadePorCliente',
  nome: 'Velocidade por cliente',
  ativa: true,
  versao: 2,
  noPerfilVigente: true,
  numeroDaVersaoVigente: 1,
  pontosVigentes: 35,
  configuracaoVigente: 'mais de 3 tentativa(s) em 10 minuto(s)',
  rascunho: {
    pontos: 45,
    configuracao: 'mais de 2 tentativa(s) em 30 minuto(s)',
    valores: { maximoDeTransacoes: 2, janelaEmMinutos: 30 },
  },
  versoes: [],
};

const SEM_RASCUNHO = {
  ...COM_RASCUNHO,
  id: '01a069e3-3333-7da4-9b84-7bfdb54799ae',
  tipo: 'NovoDispositivo',
  nome: 'Dispositivo novo',
  rascunho: null,
};

const DESATIVADA_COM_RASCUNHO = {
  ...COM_RASCUNHO,
  id: '01a069e3-4444-7da4-9b84-7bfdb54799ae',
  tipo: 'DivergenciaGeografica',
  nome: 'Divergencia geografica',
  ativa: false,
};

const PAGINA = {
  itens: [
    {
      id: '01a069e3-5555-7da4-9b84-7bfdb54799ae',
      descricao: 'Rascunho de "Velocidade por cliente"',
      regraCandidataId: COM_RASCUNHO.id,
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
  ],
  pagina: 1,
  tamanho: 25,
  totalDeItens: 1,
  totalDePaginas: 1,
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

function montar(pagina: unknown = PAGINA) {
  const chamadas: Chamada[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, opcoes?: RequestInit) => {
      chamadas.push({
        url,
        metodo: opcoes?.method ?? 'GET',
        corpo: opcoes?.body ? JSON.parse(String(opcoes.body)) : null,
      });

      if (url.includes('/api/regras/gestao')) {
        return Promise.resolve(
          json([COM_RASCUNHO, SEM_RASCUNHO, DESATIVADA_COM_RASCUNHO]),
        );
      }

      if (url.includes('/api/regras/perfil')) {
        return Promise.resolve(json(PERFIL));
      }

      if ((opcoes?.method ?? 'GET') === 'POST') {
        return Promise.resolve(json({}, 202));
      }

      return Promise.resolve(json(pagina));
    }),
  );

  render(
    <QueryClientProvider client={criarClienteDeConsultas()}>
      <MemoryRouter initialEntries={['/backtests']}>
        <PaginaDeBacktests />
      </MemoryRouter>
    </QueryClientProvider>,
  );

  return chamadas;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('lista de backtests', () => {
  it('mostra as execuções com situação e impacto', async () => {
    montar();

    const tabela = await screen.findByRole('table');

    expect(
      within(tabela).getByText(/Rascunho de "Velocidade por cliente"/),
    ).toBeInTheDocument();
    expect(within(tabela).getByText('Concluída')).toBeInTheDocument();
    expect(within(tabela).getByText('120')).toBeInTheDocument();
    expect(within(tabela).getByText('14')).toBeInTheDocument();
  });

  it('diz o que fazer quando não há nenhuma execução', async () => {
    montar({ ...PAGINA, itens: [], totalDeItens: 0 });

    expect(await screen.findByText('Nenhuma simulação ainda')).toBeInTheDocument();
  });
});

describe('pedido de simulação', () => {
  it('oferece apenas rascunhos de regras ativas', async () => {
    // Uma regra sem alteração pendente seria recusada com 409, e uma regra
    // desativada não entra no perfil. Oferecer as duas só faria a pessoa
    // descobrir isso depois de clicar.
    montar();

    await userEvent.click(
      await screen.findByRole('button', { name: 'Nova simulação' }),
    );

    const seletor = await screen.findByLabelText('Rascunho a simular');
    const opcoes = within(seletor)
      .getAllByRole('option')
      .map((o) => o.textContent);

    expect(opcoes).toHaveLength(2);
    expect(opcoes[0]).toBe('Somente os limiares');
    expect(opcoes[1]).toContain('Velocidade por cliente');
  });

  it('manda o identificador da regra, e nunca a configuração', async () => {
    // A tela não monta candidato nenhum: o servidor o compõe a partir do
    // rascunho já gravado. Se a configuração viesse daqui, o backtest seria
    // um caminho para executar regra fora do catálogo fechado.
    const chamadas = montar();

    await userEvent.click(
      await screen.findByRole('button', { name: 'Nova simulação' }),
    );

    await userEvent.selectOptions(
      await screen.findByLabelText('Rascunho a simular'),
      COM_RASCUNHO.id,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Simular' }));

    await waitFor(() => expect(chamadas.some((c) => c.metodo === 'POST')).toBe(true));

    const pedido = chamadas.find((c) => c.metodo === 'POST')!;
    const corpo = pedido.corpo as Record<string, unknown>;

    expect(pedido.url).toContain('/api/backtests');
    expect(corpo.regraId).toBe(COM_RASCUNHO.id);
    expect(corpo).not.toHaveProperty('configuracao');
    expect(corpo).not.toHaveProperty('candidato');
    expect(corpo).toHaveProperty('inicio');
    expect(corpo).toHaveProperty('fim');
  });

  it('herda os limiares do perfil em vigor quando ninguém os toca', async () => {
    const chamadas = montar();

    await userEvent.click(
      await screen.findByRole('button', { name: 'Nova simulação' }),
    );

    expect(await screen.findByLabelText('Limiar de revisão')).toHaveValue(40);
    expect(screen.getByLabelText('Limiar de bloqueio')).toHaveValue(70);

    await userEvent.click(screen.getByRole('button', { name: 'Simular' }));

    await waitFor(() => expect(chamadas.some((c) => c.metodo === 'POST')).toBe(true));

    const corpo = chamadas.find((c) => c.metodo === 'POST')!.corpo as Record<
      string,
      unknown
    >;

    // Nulo, e não 40: quem decide o padrão é o servidor, a partir do perfil
    // vigente no momento do pedido.
    expect(corpo.limiarDeRevisao).toBeNull();
    expect(corpo.limiarDeBloqueio).toBeNull();
    expect(corpo.regraId).toBeNull();
  });

  it('recusa limiares invertidos antes de chamar a API', async () => {
    const chamadas = montar();

    await userEvent.click(
      await screen.findByRole('button', { name: 'Nova simulação' }),
    );

    const revisao = await screen.findByLabelText('Limiar de revisão');

    await userEvent.clear(revisao);
    await userEvent.type(revisao, '90');

    expect(screen.getByRole('button', { name: 'Simular' })).toBeDisabled();
    expect(chamadas.some((c) => c.metodo === 'POST')).toBe(false);
  });
});

describe('contrato de backtests', () => {
  it('sabe o que ainda vai mudar sozinho', () => {
    // É o que decide se a tela repergunta. Reperguntar para sempre depois da
    // última execução seria consulta sem motivo.
    expect(estaEmAndamento('Pendente')).toBe(true);
    expect(estaEmAndamento('Executando')).toBe(true);
    expect(estaEmAndamento('Concluida')).toBe(false);
    expect(estaEmAndamento('Falhou')).toBe(false);
    expect(estaEmAndamento('Cancelada')).toBe(false);
  });

  it('só oferece cancelar o que ainda não terminou', () => {
    expect(podeCancelar('Pendente')).toBe(true);
    expect(podeCancelar('Concluida')).toBe(false);
    expect(podeCancelar('Cancelada')).toBe(false);
  });

  it('distingue ausência de investigação de investigação inconclusiva', () => {
    // São coisas diferentes: uma é "ninguém olhou", a outra é "olhamos e não
    // deu para concluir".
    expect(rotularVeredito(null)).toBe('Sem investigação');
    expect(rotularVeredito('Inconclusiva')).toBe('Inconclusiva');
    expect(rotularVeredito('FraudeConfirmada')).toBe('Fraude confirmada');
  });

  it('nomeia a direção de cada mudança', () => {
    expect(direcaoDaMudanca({ de: 'Permitir', para: 'Bloquear', quantidade: 1 })).toBe(
      'mais-rigido',
    );

    expect(direcaoDaMudanca({ de: 'Revisar', para: 'Permitir', quantidade: 1 })).toBe(
      'mais-permissivo',
    );
  });

  it('esconde a variação quando não há variação', () => {
    // Uma coluna cheia de "0" esconderia as linhas que de fato mudaram.
    expect(variacao(10, 10)).toBe('');
    expect(variacao(10, 14)).toBe('+4');
    expect(variacao(10, 6)).toBe('-4');
  });
});
