import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import { consultaDeCasos, FILTROS_DE_CASOS_INICIAIS, permite } from '../api/casos';
import type { CasoDetalhado } from '../api/casos';
import { PaginaDoCaso } from './PaginaDoCaso';

/**
 * O workspace da investigação.
 *
 * Três coisas são verificadas aqui, e as três são promessas do produto:
 *
 * 1. **A tela não decide quem pode agir.** Os botões vêm de
 *    `acoesPermitidas`, que o servidor calcula. Se a tela deduzisse, ela
 *    ofereceria ações que o backend recusaria — ou esconderia ações válidas.
 * 2. **Toda ação carrega a versão lida.** É o que impede duas pessoas de
 *    escreverem uma por cima da outra.
 * 3. **Nota é texto, nunca HTML.** O conteúdo aparece como escrito, e a
 *    marcação não vira elemento.
 */

const ID = '01a069e3-1111-7da4-9b84-7bfdb54799ae';

const CASO: CasoDetalhado = {
  id: ID,
  titulo: 'Viagem ao exterior',
  status: 'EmAnalise',
  resultado: null,
  responsavelId: '01a069e3-2222-7da4-9b84-7bfdb54799ae',
  responsavelNome: 'Ana Souza',
  abertoPorNome: 'Ana Souza',
  resolvidoPorNome: null,
  abertoEm: '2026-09-05T10:00:00Z',
  atualizadoEm: '2026-09-05T10:05:00Z',
  resolvidoEm: null,
  versao: 4,
  alertas: [
    {
      id: '01a069e3-3333-7da4-9b84-7bfdb54799ae',
      transacaoId: '01a069e3-4444-7da4-9b84-7bfdb54799ae',
      decisao: 'Revisar',
      score: 45,
      prioridade: 'Media',
      status: 'EmCaso',
      criadoEm: '2026-09-05T10:00:02Z',
      identificadorExterno: 'pedido-1001',
      valor: 210,
      moeda: 'BRL',
      clienteExternoId: 'cli-ana',
      ocorridaEm: '2026-09-05T09:59:00Z',
      sinais: [
        { tipo: 'DivergenciaGeografica', pontos: 25 },
        { tipo: 'NovoDispositivo', pontos: 20 },
      ],
    },
  ],
  timeline: [
    {
      id: 'e1',
      tipo: 'CasoAberto',
      descricao: 'Caso aberto com 1 alerta(s).',
      autorId: 'u1',
      autorNome: 'Ana Souza',
      referenciaId: null,
      ocorridoEm: '2026-09-05T10:00:00Z',
    },
    {
      id: 'e2',
      tipo: 'Atribuido',
      descricao: 'Ana Souza assumiu o caso.',
      autorId: 'u1',
      autorNome: 'Ana Souza',
      referenciaId: 'u1',
      ocorridoEm: '2026-09-05T10:05:00Z',
    },
  ],
  notas: [],
  acoesPermitidas: ['adicionarNota', 'associarAlerta', 'resolver'],
};

function json(corpo: unknown) {
  return new Response(JSON.stringify(corpo), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

interface Chamada {
  url: string;
  metodo: string;
  corpo: unknown;
}

function montar(caso: CasoDetalhado) {
  const chamadas: Chamada[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, opcoes?: RequestInit) => {
      chamadas.push({
        url,
        metodo: opcoes?.method ?? 'GET',
        corpo: opcoes?.body ? JSON.parse(String(opcoes.body)) : null,
      });

      return Promise.resolve(json(caso));
    }),
  );

  render(
    <QueryClientProvider client={criarClienteDeConsultas()}>
      <MemoryRouter initialEntries={[`/casos/${ID}`]}>
        <Routes>
          <Route path="/casos/:id" element={<PaginaDoCaso />} />
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

describe('workspace do caso', () => {
  it('reúne risco, transações e sinais numa tela só', async () => {
    montar(CASO);

    expect(await screen.findByText('Viagem ao exterior')).toBeInTheDocument();
    expect(screen.getByText('Em análise')).toBeInTheDocument();
    expect(screen.getAllByText('Ana Souza').length).toBeGreaterThan(0);

    // A transação, com o caminho para o detalhe completo.
    const ligacao = screen.getByRole('link', { name: 'pedido-1001' });

    expect(ligacao).toHaveAttribute(
      'href',
      `/transacoes/${CASO.alertas[0]!.transacaoId}`,
    );

    // Os sinais que pesaram, sem precisar abrir outra aba.
    expect(screen.getByText(/Divergência geográfica/)).toBeInTheDocument();
    expect(screen.getByText(/Dispositivo novo/)).toBeInTheDocument();
  });

  it('mostra a história do caso em ordem', async () => {
    montar(CASO);

    await screen.findByText('Viagem ao exterior');

    // As duas entradas, na ordem em que aconteceram — e a ordem vem do
    // servidor, que a monta pela sequencia, e nao pelo horario.
    const historico = screen.getByRole('list', { name: 'Histórico do caso' });
    const entradas = within(historico).getAllByRole('listitem');

    expect(entradas).toHaveLength(2);
    expect(entradas[0]).toHaveTextContent('Caso aberto com 1 alerta(s).');
    expect(entradas[1]).toHaveTextContent('Ana Souza assumiu o caso.');
  });

  it('só oferece as ações que o servidor permitiu', async () => {
    // A tela não deduz nada: um caso sem `assumir` na lista não mostra o
    // botão, mesmo estando sem responsável.
    montar({ ...CASO, acoesPermitidas: ['adicionarNota'] });

    await screen.findByText('Viagem ao exterior');

    expect(screen.queryByRole('button', { name: 'Assumir o caso' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Resolver' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Anotar' })).toBeInTheDocument();
  });

  it('não oferece ação nenhuma num caso resolvido', async () => {
    montar({
      ...CASO,
      status: 'Resolvido',
      resultado: 'Legitima',
      resolvidoPorNome: 'Ana Souza',
      resolvidoEm: '2026-09-05T11:00:00Z',
      acoesPermitidas: [],
    });

    expect(await screen.findByText('Legítima')).toBeInTheDocument();
    expect(screen.getByText(/não muda mais/)).toBeInTheDocument();
    expect(screen.queryByRole('button')).toBeNull();
  });

  it('envia a versão que leu ao assumir', async () => {
    // Sem a versão, duas pessoas trabalhando no mesmo caso escreveriam uma por
    // cima da outra e a última venceria em silêncio.
    const chamadas = montar({ ...CASO, acoesPermitidas: ['assumir'] });

    await screen.findByText('Viagem ao exterior');

    await userEvent.click(screen.getByRole('button', { name: 'Assumir o caso' }));

    await waitFor(() => expect(chamadas.some((c) => c.metodo === 'POST')).toBe(true));

    const acao = chamadas.find((c) => c.metodo === 'POST')!;

    expect(acao.url).toContain(`/api/casos/${ID}/assumir`);
    expect(acao.corpo).toEqual({ versao: 4 });
  });

  it('envia resultado e versão ao resolver', async () => {
    const chamadas = montar(CASO);

    await screen.findByText('Viagem ao exterior');

    await userEvent.selectOptions(
      screen.getByLabelText('Resultado'),
      'FraudeConfirmada',
    );
    await userEvent.click(screen.getByRole('button', { name: 'Resolver' }));

    await waitFor(() => expect(chamadas.some((c) => c.metodo === 'POST')).toBe(true));

    const acao = chamadas.find((c) => c.metodo === 'POST')!;

    expect(acao.url).toContain(`/api/casos/${ID}/resolucao`);
    expect(acao.corpo).toEqual({ resultado: 'FraudeConfirmada', versao: 4 });
  });

  it('não deixa anotar em branco', async () => {
    montar(CASO);

    await screen.findByText('Viagem ao exterior');

    expect(screen.getByRole('button', { name: 'Anotar' })).toBeDisabled();

    await userEvent.type(screen.getByLabelText('Nova nota'), 'Cliente confirmou.');

    expect(screen.getByRole('button', { name: 'Anotar' })).toBeEnabled();
  });

  it('mostra a nota como texto, e não como HTML', async () => {
    // A defesa de verdade contra XSS é a saída. Mesmo que uma nota com
    // marcação chegasse do servidor, ela apareceria como texto — nunca como
    // elemento.
    const conteudo = '<script>alert(1)</script> valor < 100';

    montar({
      ...CASO,
      notas: [
        {
          id: 'n1',
          conteudo,
          autorId: 'u1',
          autorNome: 'Ana Souza',
          criadaEm: '2026-09-05T10:10:00Z',
        },
      ],
    });

    const nota = await screen.findByText(conteudo);

    expect(nota).toBeInTheDocument();
    expect(within(nota).queryByRole('generic')).toBeNull();
    expect(document.querySelector('script')).toBeNull();
  });

  it('avisa quando a ação é recusada, em vez de engolir o erro', async () => {
    // Um conflito de versão precisa ser lido: é ele que diz "recarregue antes
    // de agir".
    vi.stubGlobal(
      'fetch',
      vi.fn((_url: string, opcoes?: RequestInit) =>
        Promise.resolve(
          (opcoes?.method ?? 'GET') === 'GET'
            ? json(CASO)
            : new Response(
                JSON.stringify({
                  title: 'Conflito de estado',
                  status: 409,
                  detail: 'O caso mudou depois que você o abriu.',
                  codigo: 'versao_desatualizada',
                }),
                {
                  status: 409,
                  headers: { 'Content-Type': 'application/problem+json' },
                },
              ),
        ),
      ),
    );

    render(
      <QueryClientProvider client={criarClienteDeConsultas()}>
        <MemoryRouter initialEntries={[`/casos/${ID}`]}>
          <Routes>
            <Route path="/casos/:id" element={<PaginaDoCaso />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await screen.findByText('Viagem ao exterior');

    await userEvent.click(screen.getByRole('button', { name: 'Resolver' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});

describe('contrato de casos', () => {
  it('omite os filtros vazios', () => {
    const consulta = consultaDeCasos(FILTROS_DE_CASOS_INICIAIS);

    expect(consulta).not.toContain('status=');
    expect(consulta).not.toContain('resultado=');
    expect(consulta).not.toContain('semResponsavel=');
  });

  it('envia os filtros preenchidos', () => {
    const consulta = consultaDeCasos({
      status: 'Novo',
      resultado: 'Legitima',
      semResponsavel: true,
      pagina: 2,
    });

    expect(consulta).toContain('status=Novo');
    expect(consulta).toContain('resultado=Legitima');
    expect(consulta).toContain('semResponsavel=true');
    expect(consulta).toContain('pagina=2');
  });

  it('responde sobre as ações a partir do que o servidor mandou', () => {
    expect(permite(CASO, 'resolver')).toBe(true);
    expect(permite(CASO, 'transferir')).toBe(false);
  });
});
