import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

// Sem isto, uma arvore renderizada por um teste continua no DOM e o proximo
// teste encontra dois elementos com o mesmo texto - falha confusa e
// intermitente, do tipo que faz suite perder credibilidade.
afterEach(() => {
  cleanup();
});
