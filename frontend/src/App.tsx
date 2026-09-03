import type { QueryClient } from '@tanstack/react-query';
import { QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter } from 'react-router';
import { criarClienteDeConsultas } from './api/clienteDeConsultas';
import { Rotas } from './rotas/Rotas';

const clientePadrao = criarClienteDeConsultas();

export function App({ cliente = clientePadrao }: { cliente?: QueryClient }) {
  return (
    <QueryClientProvider client={cliente}>
      <BrowserRouter>
        <Rotas />
      </BrowserRouter>
    </QueryClientProvider>
  );
}
