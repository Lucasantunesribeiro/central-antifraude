import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

const ALVO_DA_API = process.env.VITE_API_PROXY_TARGET ?? 'http://localhost:5175';

export default defineConfig({
  plugins: [react()],

  server: {
    port: 5174,
    proxy: {
      // Em desenvolvimento o navegador enxerga a API na mesma origem.
      //
      // Isso nao e conveniencia: a API ainda nao tem politica de CORS, e ela
      // so deve ser desenhada na Fase 11, junto do modelo de deploy real
      // (frontend na Vercel, API em Lambda). Configurar CORS agora seria
      // congelar uma decisao de seguranca antes de conhecer o desenho final.
      // Prefixo unico para as rotas de aplicacao. Sem ele, /usuarios seria ao
      // mesmo tempo uma rota do SPA e um endpoint da API - e um F5 nessa rota
      // devolveria JSON no lugar da aplicacao.
      '/api': { target: ALVO_DA_API, changeOrigin: true },
      '/health': { target: ALVO_DA_API, changeOrigin: true },
    },
  },

  build: {
    sourcemap: true,
  },

  test: {
    environment: 'jsdom',
    globals: false,
    setupFiles: ['./src/testes/preparo.ts'],
    css: false,
    restoreMocks: true,
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
