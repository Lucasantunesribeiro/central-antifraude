import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import type { RegraAdministrada } from '../api/regras';
import { PaginaDaRegra } from './PaginaDaRegra';

/**
 * A tela da regra, onde o rascunho vira versão.
 *
 * Três promessas são verificadas aqui:
 *
 * 1. **o que vale, o que está escrito e o que já valeu aparecem separados** —
 *    confundi-los faria o Supervisor achar que mudou o motor quando só
 *    escreveu um rascunho;
 * 2. **toda ação carrega a versão lida**, que é o que impede dois supervisores
 *    de publicarem um por cima do outro;
 * 3. **o histórico é somente leitura** — não há botão que altere uma versão
 *    publicada, porque é ela que explica as avaliações antigas.
 */

const ID = '01a069e3-2222-7da4-9b84-7bfdb54799ae';

const TIPOS = [
  {
    tipo: 'NovoDispositivo',
    rotulo: 'Dispositivo novo',
    resumo: 'Dispositivo nunca visto antes para aquele cliente.',
    pontosSugeridos: 20,
    campos: [
      {
        nome: 'minimoDeTransacoesNoHistorico',
        rotulo: 'Minimo de transacoes no historico',
        tipo: 'Inteiro',
        minimo: 1,
        maximo: 100,
        padrao: 3,
      },
    ],
  },
];

const REGRA: RegraAdministrada = {
  id: ID,
  tipo: 'NovoDispositivo',
  nome: 'Dispositivo novo',
  ativa: true,
  versao: 4,
  noPerfilVigente: true,
  numeroDaVersaoVigente: 2,
  pontosVigentes: 20,
  configuracaoVigente: 'dispositivo nao visto antes, com ao menos 3 transacao(oes)',
  rascunho: null,
  versoes: [
    {
      id: 'v2',
      numero: 2,
      pontos: 20,
      configuracao: 'dispositivo nao visto antes, com ao menos 3 transacao(oes)',
      valores: { minimoDeTransacoesNoHistorico: 3 },
      publicadaEm: '2026-09-06T10:00:00Z',
    },
    {
      id: 'v1',
      numero: 1,
      pontos: 15,
      configuracao: 'dispositivo nao visto antes, com ao menos 5 transacao(oes)',
      valores: { minimoDeTransacoesNoHistorico: 5 },
      publicadaEm: '2026-09-01T10:00:00Z',
    },
  ],
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

function montar(regra: RegraAdministrada) {
  const chamadas: Chamada[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, opcoes?: RequestInit) => {
      chamadas.push({
        url,
        metodo: opcoes?.method ?? 'GET',
        corpo: opcoes?.body ? JSON.parse(String(opcoes.body)) : null,
      });

      if (url.includes('/api/regras/tipos')) {
        return Promise.resolve(json(TIPOS));
      }

      return Promise.resolve(json(regra));
    }),
  );

  render(
    <QueryClientProvider client={criarClienteDeConsultas()}>
      <MemoryRouter initialEntries={[`/regras/${ID}`]}>
        <Routes>
          <Route path="/regras/:id" element={<PaginaDaRegra />} />
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

describe('detalhe da regra', () => {
  it('separa o que vale do que já valeu', async () => {
    montar(REGRA);

    expect(
      await screen.findByRole('heading', { name: 'Dispositivo novo' }),
    ).toBeInTheDocument();

    // O que está em vigor.
    expect(screen.getByText(/Versão 2 · \+20 ponto/)).toBeInTheDocument();

    // E o histórico completo, da mais recente para a mais antiga.
    const tabela = screen.getByRole('table');
    const linhas = within(tabela).getAllByRole('row').slice(1);

    expect(linhas).toHaveLength(2);
    expect(linhas[0]).toHaveTextContent('2');
    expect(linhas[1]).toHaveTextContent('+15');
  });

  it('não oferece nenhum caminho para alterar uma versão publicada', async () => {
    // A imutabilidade não é convenção: não há rota nem botão. É a versão
    // publicada que explica as avaliações antigas.
    montar(REGRA);

    await screen.findByRole('heading', { name: 'Dispositivo novo' });

    const tabela = screen.getByRole('table');

    expect(within(tabela).queryByRole('button')).toBeNull();
    expect(within(tabela).queryByRole('textbox')).toBeNull();
  });

  it('diz quando não há alterações pendentes', async () => {
    montar(REGRA);

    expect(await screen.findByText(/Não há alterações pendentes/)).toBeInTheDocument();

    // Sem rascunho, não há o que publicar nem o que descartar.
    expect(screen.getByRole('button', { name: 'Publicar versão' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Descartar rascunho' })).toBeDisabled();
  });

  it('abre o formulário com o rascunho, e não com a versão publicada', async () => {
    // O rascunho é o que a pessoa estava escrevendo. Abrir com o valor
    // publicado apagaria o trabalho dela no primeiro salvamento.
    montar({
      ...REGRA,
      rascunho: {
        pontos: 45,
        configuracao: 'dispositivo nao visto antes, com ao menos 9 transacao(oes)',
        valores: { minimoDeTransacoesNoHistorico: 9 },
      },
    });

    await screen.findByRole('heading', { name: 'Dispositivo novo' });

    // O editor só aparece depois que o contrato do tipo chega do servidor.
    expect(
      await screen.findByLabelText('Minimo de transacoes no historico'),
    ).toHaveValue(9);
    expect(screen.getByLabelText('Pontos')).toHaveValue(45);
    expect(
      screen.getByText(/alterações escritas e não publicadas/),
    ).toBeInTheDocument();
  });

  it('envia a versão que leu ao salvar o rascunho', async () => {
    const chamadas = montar(REGRA);

    await userEvent.click(
      await screen.findByRole('button', { name: 'Salvar rascunho' }),
    );

    await waitFor(() => expect(chamadas.some((c) => c.metodo === 'PUT')).toBe(true));

    const acao = chamadas.find((c) => c.metodo === 'PUT')!;

    expect(acao.url).toContain(`/api/regras/${ID}/rascunho`);
    expect(acao.corpo).toEqual({
      nome: 'Dispositivo novo',
      configuracao: { minimoDeTransacoesNoHistorico: 3 },
      pontos: 20,
      versao: 4,
    });
  });

  it('envia a versão que leu ao publicar', async () => {
    const chamadas = montar({
      ...REGRA,
      rascunho: {
        pontos: 45,
        configuracao: 'dispositivo nao visto antes, com ao menos 9 transacao(oes)',
        valores: { minimoDeTransacoesNoHistorico: 9 },
      },
    });

    await userEvent.click(
      await screen.findByRole('button', { name: 'Publicar versão' }),
    );

    await waitFor(() =>
      expect(chamadas.some((c) => c.url.includes('/publicacao'))).toBe(true),
    );

    const acao = chamadas.find((c) => c.url.includes('/publicacao'))!;

    expect(acao.metodo).toBe('POST');
    expect(acao.corpo).toEqual({ versao: 4 });
  });

  it('envia a versão que leu ao desativar', async () => {
    const chamadas = montar(REGRA);

    await screen.findByRole('heading', { name: 'Dispositivo novo' });

    await userEvent.click(screen.getByRole('button', { name: 'Desativar regra' }));

    await waitFor(() =>
      expect(chamadas.some((c) => c.url.includes('/ativacao'))).toBe(true),
    );

    const acao = chamadas.find((c) => c.url.includes('/ativacao'))!;

    expect(acao.corpo).toEqual({ ativa: false, versao: 4 });
  });

  it('avisa que a regra desativada saiu do perfil, sem esconder a versão', async () => {
    montar({ ...REGRA, ativa: false, noPerfilVigente: false });

    expect(await screen.findByText(/fora do perfil em vigor/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reativar regra' })).toBeInTheDocument();

    // O histórico continua ali: desativar não apaga nada.
    expect(within(screen.getByRole('table')).getAllByRole('row')).toHaveLength(3);
  });

  it('avisa quando a ação é recusada, em vez de engolir o erro', async () => {
    // Um conflito de versão precisa ser lido: é ele que diz "recarregue antes
    // de publicar".
    vi.stubGlobal(
      'fetch',
      vi.fn((url: string, opcoes?: RequestInit) => {
        if (url.includes('/api/regras/tipos')) {
          return Promise.resolve(json(TIPOS));
        }

        if ((opcoes?.method ?? 'GET') === 'GET') {
          return Promise.resolve(json(REGRA));
        }

        return Promise.resolve(
          new Response(
            JSON.stringify({
              title: 'Conflito de estado',
              status: 409,
              detail: 'A regra mudou depois que você a abriu.',
              codigo: 'versao_desatualizada',
            }),
            { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
          ),
        );
      }),
    );

    render(
      <QueryClientProvider client={criarClienteDeConsultas()}>
        <MemoryRouter initialEntries={[`/regras/${ID}`]}>
          <Routes>
            <Route path="/regras/:id" element={<PaginaDaRegra />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await screen.findByRole('heading', { name: 'Dispositivo novo' });

    await userEvent.click(screen.getByRole('button', { name: 'Desativar regra' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'A regra mudou depois que você a abriu.',
    );
  });
});
