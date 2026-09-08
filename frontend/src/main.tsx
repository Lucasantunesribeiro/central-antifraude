// Fontes auto-hospedadas, e nao de CDN externo: uma requisicao a terceiro em
// toda carga de pagina seria dependencia de disponibilidade alheia, ponto de
// rastreamento do usuario e mais uma origem para a politica de conteudo
// permitir. O peso vem no bundle, que ja e servido pela Vercel.
import '@fontsource-variable/inter';
import '@fontsource-variable/jetbrains-mono';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import './estilos/global.css';

const raiz = document.getElementById('root');

if (!raiz) {
  throw new Error('Elemento #root nao encontrado em index.html.');
}

createRoot(raiz).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
