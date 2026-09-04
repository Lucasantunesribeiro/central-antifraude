import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { criarClienteDeConsultas } from '../api/clienteDeConsultas';
import type { TransacaoDetalhada } from '../api/risco';
import { PaginaDeTransacao } from './PaginaDeTransacao';
import { PaginaDeTransacoes } from './PaginaDeTransacoes';

/**
 * O que estas telas prometem ao analista: a decisão nunca aparece sozinha.
 *
 * Um score de 45 sem explicação não serve para investigar nada — ele diz que
 * algo pesou, mas não o quê. Por isso os testes aqui olham para a
 * explicabilidade: os sinais estão na tela, com a contribuição de cada um e a
 * versão da regra que os produziu.
 *
 * O outro eixo é o que NÃO pode aparecer: a tela não inventa decisão quando a
 * avaliação não existe, e não mostra dado sensível que o backend escolheu não
 * guardar.
 */

const ID = '01a069e3-9970-7da4-9b84-7bfdb54799ae';

const DETALHE: TransacaoDetalhada = {
  id: ID,
  identificadorExterno: 'pedido-1001',
  valor: 4900,
  moeda: 'BRL',
  ocorridaEm: '2026-09-04T10:00:00Z',
  recebidaEm: '2026-09-04T10:00:02Z',
  clienteExternoId: 'cli-777',
  referenciaDoInstrumento: 'pi_demo_123',
  fingerprintDoDispositivo: 'disp-de-viagem',
  paisDeOrigem: 'PT',
  avaliacao: {
    id: '01a069e3-bbbb-7da4-9b84-7bfdb54799ae',
    score: 45,
    decisao: 'Revisar',
    avaliadaEm: '2026-09-04T10:00:02Z',
    versaoDePerfilId: '01a069e3-cccc-7da4-9b84-7bfdb54799ae',
    numeroDaVersaoDePerfil: 1,
    versaoDoMotor: '1.0',
    somaBrutaDosPontos: 45,
    scoreFoiLimitado: false,
    sinais: [
      {
        tipo: 'DivergenciaGeografica',
        explicacao: 'Origem em PT, diferente do historico deste cliente (BR).',
        pontos: 25,
        regraId: '01a069e3-dddd-7da4-9b84-7bfdb54799ae',
        versaoDaRegra: 1,
        evidencia: { paisDaTransacao: 'PT', paisesConhecidos: 'BR' },
      },
      {
        tipo: 'NovoDispositivo',
        explicacao:
          'Dispositivo nao visto nas 3 transacao(oes) anteriores deste cliente.',
        pontos: 20,
        regraId: '01a069e3-eeee-7da4-9b84-7bfdb54799ae',
        versaoDaRegra: 1,
        evidencia: { transacoesNoHistorico: '3' },
      },
    ],
  },
};

function json(corpo: unknown, status = 200) {
  return new Response(JSON.stringify(corpo), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function montarDetalhe(dados: TransacaoDetalhada) {
  vi.stubGlobal(
    'fetch',
    vi.fn(() => Promise.resolve(json(dados))),
  );

  return render(
    <QueryClientProvider client={criarClienteDeConsultas()}>
      <MemoryRouter initialEntries={[`/transacoes/${ID}`]}>
        <Routes>
          <Route path="/transacoes/:id" element={<PaginaDeTransacao />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('detalhe da transação', () => {
  it('mostra a decisão junto dos sinais que a produziram', async () => {
    montarDetalhe(DETALHE);

    expect(await screen.findByText('Revisar')).toBeInTheDocument();
    expect(screen.getByText('45')).toBeInTheDocument();

    // Os dois sinais, com o nome legível e a contribuição de cada um.
    expect(screen.getByText('Divergência geográfica')).toBeInTheDocument();
    expect(screen.getByText('+25')).toBeInTheDocument();
    expect(screen.getByText('Dispositivo novo')).toBeInTheDocument();
    expect(screen.getByText('+20')).toBeInTheDocument();

    // A explicação em texto: é o que o analista lê para decidir.
    expect(
      screen.getByText(/Origem em PT, diferente do historico/),
    ).toBeInTheDocument();

    // E a versão da regra, que sustenta a leitura histórica.
    expect(screen.getAllByText('Regra versão 1')).toHaveLength(2);
  });

  it('avisa quando o score foi limitado a 100', async () => {
    // Sem o aviso, alguém somaria +60 +60 na tela, chegaria a 120 e acharia
    // que o sistema errou uma conta simples.
    montarDetalhe({
      ...DETALHE,
      avaliacao: {
        ...DETALHE.avaliacao!,
        score: 100,
        decisao: 'Bloquear',
        somaBrutaDosPontos: 120,
        scoreFoiLimitado: true,
      },
    });

    expect(await screen.findByText(/As regras somaram 120 pontos/)).toBeInTheDocument();
  });

  it('não inventa decisão quando a transação não tem avaliação', async () => {
    montarDetalhe({ ...DETALHE, avaliacao: null });

    expect(
      await screen.findByText('Esta transação não tem avaliação.'),
    ).toBeInTheDocument();

    expect(screen.queryByText('Permitir')).not.toBeInTheDocument();
    expect(screen.queryByText('Revisar')).not.toBeInTheDocument();
    expect(screen.queryByText('Bloquear')).not.toBeInTheDocument();
  });

  it('não exibe endereço nem fingerprint de IP', async () => {
    // O endereço bruto nunca chega ao banco, e o HMAC derivado dele não ajuda
    // a investigação — exibi-lo só ampliaria a superfície de dado sensível.
    const { container } = montarDetalhe(DETALHE);

    await screen.findByText('Revisar');

    expect(container.textContent).not.toMatch(/fingerprint\s*de\s*ip/i);
    expect(container.textContent).not.toMatch(/\d+\.\d+\.\d+\.\d+/);
  });
});

describe('listagem de transações', () => {
  it('mostra score e decisão de cada linha e diz quando não há avaliação', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          json({
            total: 2,
            itens: [
              {
                id: ID,
                identificadorExterno: 'pedido-1001',
                valor: 4900,
                moeda: 'BRL',
                ocorridaEm: '2026-09-04T10:00:00Z',
                recebidaEm: '2026-09-04T10:00:02Z',
                clienteExternoId: 'cli-777',
                paisDeOrigem: 'PT',
                score: 45,
                decisao: 'Revisar',
              },
              {
                id: '01a069e3-ffff-7da4-9b84-7bfdb54799ae',
                identificadorExterno: 'pedido-antigo',
                valor: 100,
                moeda: 'BRL',
                ocorridaEm: '2026-09-01T10:00:00Z',
                recebidaEm: '2026-09-01T10:00:01Z',
                clienteExternoId: 'cli-000',
                paisDeOrigem: 'BR',
                score: null,
                decisao: null,
              },
            ],
          }),
        ),
      ),
    );

    render(
      <QueryClientProvider client={criarClienteDeConsultas()}>
        <MemoryRouter>
          <PaginaDeTransacoes />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    expect(await screen.findByText('Revisar')).toBeInTheDocument();
    expect(screen.getByText('45')).toBeInTheDocument();

    // A linha sem avaliação diz isso, em vez de mostrar zero.
    expect(screen.getByText('sem avaliação')).toBeInTheDocument();
  });
});
