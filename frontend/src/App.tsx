import type { QueryClient } from '@tanstack/react-query';
import { QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter } from 'react-router';
import { criarClienteDeConsultas } from './api/clienteDeConsultas';
import { Rotas } from './rotas/Rotas';
import { ProvedorDeSessao } from './sessao/ProvedorDeSessao';

const clientePadrao = criarClienteDeConsultas();

export function App({ cliente = clientePadrao }: { cliente?: QueryClient }) {
  return (
    <QueryClientProvider client={cliente}>
      <BrowserRouter>
        {/* A sessao fica dentro do Router: a restauracao precisa poder
            redirecionar, e o logout precisa navegar. */}
        <ProvedorDeSessao>
          <Rotas />
        </ProvedorDeSessao>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
