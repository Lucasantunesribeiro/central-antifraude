import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import { ProvedorDeSessao } from '../sessao/ProvedorDeSessao';
import {
  foraDaFaixa,
  situacaoDaRegra,
  valoresIniciais,
  type CampoDeConfiguracao,
  type RegraAdministrada,
} from '../api/regras';
import { PaginaDeRegras } from './PaginaDeRegras';

/**
 * A tela de regras é duas telas em uma, e o que estes testes protegem é
 * justamente a diferença entre elas.
 *
 * Para o analista e o auditor, regras são leitura: eles precisam do catálogo
 * para entender o próprio score. Para a supervisão, é administração.
 *
 * A tela não decide isso sozinha — ela só evita oferecer o que o backend
 * recusaria. Quem autoriza é a API, que responde `403` (CLAUDE.md seção 52).
 */

const PERFIL = {
  versaoId: '01a069e3-1111-7da4-9b84-7bfdb54799ae',
  numero: 3,
  limiarDeRevisao: 40,
  limiarDeBloqueio: 70,
  publicadaEm: '2026-09-06T10:00:00Z',
  regras: [
    {
      id: '01a069e3-2222-7da4-9b84-7bfdb54799ae',
      tipo: 'VelocidadePorCliente',
      nome: 'Velocidade por cliente',
      versaoAtual: 1,
      pontos: 35,
      configuracao: 'mais de 3 tentativa(s) em 10 minuto(s)',
      publicadaEm: '2026-09-06T10:00:00Z',
    },
  ],
};

const GESTAO: RegraAdministrada[] = [
  {
    id: '01a069e3-2222-7da4-9b84-7bfdb54799ae',
    tipo: 'VelocidadePorCliente',
    nome: 'Velocidade por cliente',
    ativa: true,
    versao: 2,
    noPerfilVigente: true,
    numeroDaVersaoVigente: 1,
    pontosVigentes: 35,
    configuracaoVigente: 'mais de 3 tentativa(s) em 10 minuto(s)',
    rascunho: null,
    versoes: [],
  },
  {
    id: '01a069e3-3333-7da4-9b84-7bfdb54799ae',
    tipo: 'NovoDispositivo',
    nome: 'Dispositivo novo',
    ativa: true,
    versao: 1,
    noPerfilVigente: false,
    numeroDaVersaoVigente: null,
    pontosVigentes: null,
    configuracaoVigente: null,
    rascunho: {
      pontos: 20,
      configuracao: 'dispositivo nao visto antes',
      valores: { minimoDeTransacoesNoHistorico: 3 },
    },
    versoes: [],
  },
];

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

function json(corpo: unknown, status = 200) {
  return new Response(JSON.stringify(corpo), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function sessao(perfil: string) {
  return json({
    accessToken: 'token-de-teste',
    expiraEm: new Date(Date.now() + 15 * 60_000).toISOString(),
    usuario: {
      id: '01a0699c-68c5-7e77-b49d-62a43bee10a8',
      email: 'pessoa@teste.local',
      nomeCompleto: 'Pessoa de Teste',
      perfil,
      organizacaoId: '01a0699c-676d-78ca-a854-bfc4a78c1fa6',
    },
  });
}

interface Chamada {
  url: string;
  metodo: string;
  corpo: unknown;
}

function montar(perfil: string) {
  const chamadas: Chamada[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, opcoes?: RequestInit) => {
      const metodo = opcoes?.method ?? 'GET';

      chamadas.push({
        url,
        metodo,
        corpo: opcoes?.body ? JSON.parse(String(opcoes.body)) : null,
      });

      if (url.includes('/api/auth/refresh')) {
        return Promise.resolve(sessao(perfil));
      }

      if (url.includes('/api/regras/tipos')) {
        return Promise.resolve(json(TIPOS));
      }

      if (url.includes('/api/regras/gestao')) {
        return Promise.resolve(json(GESTAO));
      }

      if (url.includes('/api/regras/perfil')) {
        return Promise.resolve(json(PERFIL));
      }

      return Promise.resolve(json(GESTAO[1]!, 201));
    }),
  );

  render(
    <QueryClientProvider client={criarClienteDeConsultas()}>
      <MemoryRouter initialEntries={['/regras']}>
        <ProvedorDeSessao>
          <PaginaDeRegras />
        </ProvedorDeSessao>
      </MemoryRouter>
    </QueryClientProvider>,
  );

  return chamadas;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('regras para quem não administra', () => {
  it('mostra os limiares e as regras em vigor, sem ação nenhuma', async () => {
    montar('AnalistaDeFraude');

    expect(await screen.findByText(/Perfil de risco — versão 3/)).toBeInTheDocument();

    // As três faixas, para o analista entender de onde veio a decisão.
    expect(screen.getByText('0 a 39')).toBeInTheDocument();
    expect(screen.getByText('40 a 69')).toBeInTheDocument();
    expect(screen.getByText('70 a 100')).toBeInTheDocument();

    const tabela = await screen.findByRole('table');

    expect(within(tabela).getByText('Velocidade por cliente')).toBeInTheDocument();

    // E nenhum caminho para mudar o motor.
    expect(screen.queryByRole('button', { name: 'Ajustar limiares' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Nova regra' })).toBeNull();
  });

  it('não pede a lista administrativa quando o perfil não administra', async () => {
    // Não é segurança — é não provocar um 403 previsível a cada carregamento
    // da tela, que poluiria o log do servidor com falhas que não são falhas.
    const chamadas = montar('Auditor');

    await screen.findByText(/Perfil de risco/);

    expect(chamadas.some((c) => c.url.includes('/api/regras/gestao'))).toBe(false);
  });
});

describe('regras para a supervisão', () => {
  it('distingue o que está em vigor do que é só rascunho', async () => {
    // Confundir os dois faria o Supervisor achar que configurou o motor
    // quando só escreveu um rascunho.
    montar('SupervisorDeFraude');

    const tabela = await screen.findByRole('table');

    expect(within(tabela).getByText('Em vigor')).toBeInTheDocument();
    expect(within(tabela).getByText('Rascunho')).toBeInTheDocument();
  });

  it('envia a versão vigente do perfil ao publicar limiares', async () => {
    // O número da versão é o token de concorrência do perfil: sem ele, dois
    // supervisores ajustando limiares se sobrescreveriam em silêncio.
    const chamadas = montar('SupervisorDeFraude');

    await screen.findByText(/Perfil de risco/);

    await userEvent.click(screen.getByRole('button', { name: 'Ajustar limiares' }));
    await userEvent.click(screen.getByRole('button', { name: 'Publicar limiares' }));

    await waitFor(() =>
      expect(chamadas.some((c) => c.url.includes('/perfil/limiares'))).toBe(true),
    );

    const acao = chamadas.find((c) => c.url.includes('/perfil/limiares'))!;

    expect(acao.metodo).toBe('POST');
    expect(acao.corpo).toEqual({
      limiarDeRevisao: 40,
      limiarDeBloqueio: 70,
      numeroDaVersaoVigente: 3,
    });
  });

  it('recusa limiares invertidos antes de chamar a API', async () => {
    // Revisão acima do bloqueio deixaria a faixa "revisar" vazia. O backend
    // também recusa; avisar aqui evita perder o que foi digitado num 400.
    const chamadas = montar('SupervisorDeFraude');

    await screen.findByText(/Perfil de risco/);

    await userEvent.click(screen.getByRole('button', { name: 'Ajustar limiares' }));

    const revisao = screen.getByLabelText('Limiar de revisão');

    await userEvent.clear(revisao);
    await userEvent.type(revisao, '90');

    expect(screen.getByRole('button', { name: 'Publicar limiares' })).toBeDisabled();
    expect(chamadas.some((c) => c.url.includes('/perfil/limiares'))).toBe(false);
  });

  it('monta o formulário de nova regra a partir do contrato do servidor', async () => {
    // A tela não conhece regra nenhuma: rótulo, faixa e padrão vêm de
    // /api/regras/tipos. Repetir isso aqui criaria uma segunda lista de
    // limites que sairia de sincronia no primeiro ajuste do backend.
    const chamadas = montar('SupervisorDeFraude');

    // A lista administrativa é uma segunda consulta: esperar por ela é o que
    // torna o teste independente da ordem em que as duas respondem.
    await userEvent.click(await screen.findByRole('button', { name: 'Nova regra' }));

    await userEvent.selectOptions(
      await screen.findByLabelText('Tipo'),
      'NovoDispositivo',
    );

    const campo = screen.getByLabelText('Minimo de transacoes no historico');

    expect(campo).toHaveValue(3);
    expect(campo).toHaveAttribute('min', '1');
    expect(campo).toHaveAttribute('max', '100');

    // Os pontos sugeridos também vêm do servidor.
    expect(screen.getByLabelText('Pontos')).toHaveValue(20);

    await userEvent.type(screen.getByLabelText('Nome'), 'Dispositivo raro');
    await userEvent.click(screen.getByRole('button', { name: 'Salvar rascunho' }));

    await waitFor(() =>
      expect(
        chamadas.some((c) => c.metodo === 'POST' && c.url.endsWith('/api/regras')),
      ).toBe(true),
    );

    const criacao = chamadas.find(
      (c) => c.metodo === 'POST' && c.url.endsWith('/api/regras'),
    )!;

    expect(criacao.corpo).toEqual({
      tipo: 'NovoDispositivo',
      nome: 'Dispositivo raro',
      configuracao: { minimoDeTransacoesNoHistorico: 3 },
      pontos: 20,
    });
  });
});

describe('contrato de regras', () => {
  const campo: CampoDeConfiguracao = {
    nome: 'minimoDeTransacoesNoHistorico',
    rotulo: 'Mínimo',
    tipo: 'Inteiro',
    minimo: 1,
    maximo: 100,
    padrao: 3,
  };

  it('nomeia a situação de cada regra', () => {
    expect(situacaoDaRegra(GESTAO[0]!)).toBe('Em vigor');
    expect(situacaoDaRegra(GESTAO[1]!)).toBe('Rascunho');

    expect(situacaoDaRegra({ ...GESTAO[0]!, ativa: false })).toBe('Desativada');

    expect(situacaoDaRegra({ ...GESTAO[0]!, rascunho: GESTAO[1]!.rascunho })).toBe(
      'Alterações pendentes',
    );
  });

  it('prefere o rascunho aos valores publicados ao abrir o formulário', () => {
    const tipo = { ...TIPOS[0]!, campos: [campo] };

    expect(valoresIniciais(tipo)).toEqual({ minimoDeTransacoesNoHistorico: 3 });
    expect(valoresIniciais(tipo, { minimoDeTransacoesNoHistorico: 9 })).toEqual({
      minimoDeTransacoesNoHistorico: 9,
    });
  });

  it('reconhece valor fora da faixa declarada pelo servidor', () => {
    expect(foraDaFaixa(campo, 3)).toBe(false);
    expect(foraDaFaixa(campo, 0)).toBe(true);
    expect(foraDaFaixa(campo, 101)).toBe(true);
    expect(foraDaFaixa(campo, 2.5)).toBe(true);
    expect(foraDaFaixa(campo, Number.NaN)).toBe(true);
  });
});
