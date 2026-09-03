import { afterEach, describe, expect, it, vi } from 'vitest';
import { CABECALHO_DE_CORRELACAO, requisitar } from './clienteHttp';
import { ErroDaApi } from './erros';

/**
 * O cliente HTTP e o unico lugar que traduz o contrato da API. Se ele errar,
 * toda tela erra junto - por isso ele e testado sozinho, sem React.
 */

function responder(
  corpo: unknown,
  init: {
    status?: number;
    contentType?: string;
    cabecalhos?: Record<string, string>;
  } = {},
): Response {
  const { status = 200, contentType = 'application/json', cabecalhos = {} } = init;

  return new Response(corpo === undefined ? null : JSON.stringify(corpo), {
    status,
    headers: { 'Content-Type': contentType, ...cabecalhos },
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('requisitar', () => {
  it('devolve o corpo em JSON quando a resposta e bem-sucedida', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(responder({ estado: 'Healthy' })));

    await expect(requisitar<{ estado: string }>('/health/ready')).resolves.toEqual({
      estado: 'Healthy',
    });
  });

  it('envia um identificador de correlacao em toda requisicao', async () => {
    const espiao = vi.fn().mockResolvedValue(responder({}));
    vi.stubGlobal('fetch', espiao);

    await requisitar('/health/ready');

    const cabecalhos = espiao.mock.calls[0]?.[1]?.headers as Record<string, string>;
    expect(cabecalhos[CABECALHO_DE_CORRELACAO]).toBeTruthy();
    expect(cabecalhos[CABECALHO_DE_CORRELACAO]!.length).toBeGreaterThanOrEqual(8);
  });

  it('traduz Problem Details de validacao em ErroDaApi com os campos', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        responder(
          {
            title: 'Requisicao invalida',
            status: 400,
            detail: 'A requisicao contem campos invalidos.',
            codigo: 'validacao_falhou',
            erros: { valor: ['Deve ser maior que zero.'] },
            idDeCorrelacao: 'abc-12345678',
          },
          { status: 400, contentType: 'application/problem+json' },
        ),
      ),
    );

    const erro = await requisitar('/qualquer').catch((e: unknown) => e);

    expect(erro).toBeInstanceOf(ErroDaApi);
    const daApi = erro as ErroDaApi;
    expect(daApi.categoria).toBe('validacao');
    expect(daApi.status).toBe(400);
    expect(daApi.codigo).toBe('validacao_falhou');
    expect(daApi.errosPorCampo).toEqual({ valor: ['Deve ser maior que zero.'] });
    expect(daApi.idDeCorrelacao).toBe('abc-12345678');
    expect(daApi.valeTentarDeNovo).toBe(false);
  });

  it.each([
    [401, 'naoAutenticado'],
    [403, 'naoAutorizado'],
    [404, 'naoEncontrado'],
    [409, 'conflito'],
    [429, 'limiteDeRequisicoes'],
    [500, 'servidor'],
    [503, 'servidor'],
  ])('classifica o status %i como %s', async (status, categoria) => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(responder({ status }, { status })),
    );

    const erro = (await requisitar('/qualquer').catch((e: unknown) => e)) as ErroDaApi;

    expect(erro.categoria).toBe(categoria);
  });

  it('prefere o identificador de correlacao do cabecalho ao do corpo', async () => {
    // O cabecalho existe mesmo quando o corpo veio vazio ou ilegivel, entao e
    // a fonte mais confiavel para o codigo que o usuario passa ao suporte.
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        responder(
          { idDeCorrelacao: 'do-corpo-000' },
          {
            status: 500,
            contentType: 'application/problem+json',
            cabecalhos: { [CABECALHO_DE_CORRELACAO]: 'do-cabecalho-1' },
          },
        ),
      ),
    );

    const erro = (await requisitar('/qualquer').catch((e: unknown) => e)) as ErroDaApi;

    expect(erro.idDeCorrelacao).toBe('do-cabecalho-1');
  });

  it('nao quebra quando o corpo de erro nao e JSON valido', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response('<html>gateway timeout</html>', {
          status: 504,
          headers: { 'Content-Type': 'text/html' },
        }),
      ),
    );

    const erro = (await requisitar('/qualquer').catch((e: unknown) => e)) as ErroDaApi;

    expect(erro).toBeInstanceOf(ErroDaApi);
    expect(erro.categoria).toBe('servidor');
    expect(erro.message).toContain('504');
  });

  it('transforma falha de rede em erro que vale repetir', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));

    const erro = (await requisitar('/qualquer').catch((e: unknown) => e)) as ErroDaApi;

    expect(erro.categoria).toBe('rede');
    expect(erro.valeTentarDeNovo).toBe(true);
  });

  it('serializa o corpo e declara Content-Type ao enviar dados', async () => {
    const espiao = vi.fn().mockResolvedValue(responder({}, { status: 200 }));
    vi.stubGlobal('fetch', espiao);

    await requisitar('/qualquer', { metodo: 'POST', corpo: { valor: 10 } });

    const opcoes = espiao.mock.calls[0]?.[1] as RequestInit;
    expect(opcoes.method).toBe('POST');
    expect(opcoes.body).toBe('{"valor":10}');
    expect((opcoes.headers as Record<string, string>)['Content-Type']).toBe(
      'application/json',
    );
  });

  it('respeita o cancelamento de quem chamou', async () => {
    const controlador = new AbortController();
    controlador.abort();

    vi.stubGlobal(
      'fetch',
      vi.fn((_: string, opcoes: RequestInit) => {
        if (opcoes.signal?.aborted) {
          return Promise.reject(new DOMException('Aborted', 'AbortError'));
        }
        return Promise.resolve(responder({}));
      }),
    );

    const erro = (await requisitar('/qualquer', { sinal: controlador.signal }).catch(
      (e: unknown) => e,
    )) as ErroDaApi;

    expect(erro).toBeInstanceOf(ErroDaApi);
    expect(erro.categoria).toBe('rede');
  });
});
