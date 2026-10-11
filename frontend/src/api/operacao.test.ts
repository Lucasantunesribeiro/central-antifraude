import { describe, expect, it } from 'vitest';
import { DIAS_MAXIMOS_DO_PAINEL, periodoComAtividade } from './operacao';

const DIA_EM_MS = 24 * 60 * 60 * 1000;

describe('periodoComAtividade', () => {
  it('cobre todo o cenário quando ele cabe no teto', () => {
    const { de, ate } = periodoComAtividade(
      '2026-09-07T00:00:00.000Z',
      '2026-09-09T00:00:00.000Z',
    );

    // Histórico curto (o caso da demonstração): a janela é o cenário inteiro.
    expect(de).toBe('2026-09-07T00:00:00.000Z');
    expect(ate).toBe('2026-09-09T00:00:00.000Z');
  });

  it('limita ao teto quando o histórico é mais longo que ele', () => {
    const primeira = '2026-01-01T00:00:00.000Z';
    const ultima = '2026-10-01T00:00:00.000Z';

    const { de, ate } = periodoComAtividade(primeira, ultima);

    // Termina na última atividade e recua no máximo o teto — nunca pede um
    // intervalo que o backend recusaria.
    expect(ate).toBe(ultima);
    const span = new Date(ate).getTime() - new Date(de).getTime();
    expect(span).toBe(DIAS_MAXIMOS_DO_PAINEL * DIA_EM_MS);
    expect(new Date(de).getTime()).toBeGreaterThan(new Date(primeira).getTime());
  });
});
