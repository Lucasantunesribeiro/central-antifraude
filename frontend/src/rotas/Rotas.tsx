import { Route, Routes } from 'react-router';
import { LayoutDoApp } from '../layout/LayoutDoApp';
import { PaginaDeLogin } from '../paginas/PaginaDeLogin';
import { PaginaDeIntegracoes } from '../paginas/PaginaDeIntegracoes';
import { PaginaDeRegras } from '../paginas/PaginaDeRegras';
import { PaginaDeTransacao } from '../paginas/PaginaDeTransacao';
import { PaginaDeTransacoes } from '../paginas/PaginaDeTransacoes';
import { PaginaDeUsuarios } from '../paginas/PaginaDeUsuarios';
import { PaginaDoPainel } from '../paginas/PaginaDoPainel';
import { PaginaInicial } from '../paginas/PaginaInicial';
import { PaginaNaoEncontrada } from '../paginas/PaginaNaoEncontrada';
import { RotaProtegida } from './RotaProtegida';

export function Rotas() {
  return (
    <Routes>
      <Route element={<LayoutDoApp />}>
        <Route index element={<PaginaInicial />} />
        <Route path="entrar" element={<PaginaDeLogin />} />

        <Route element={<RotaProtegida />}>
          <Route path="painel" element={<PaginaDoPainel />} />
          {/* Transacoes sao leitura: qualquer perfil autenticado enxerga as
              da propria organizacao. */}
          <Route path="transacoes" element={<PaginaDeTransacoes />} />
          <Route path="transacoes/:id" element={<PaginaDeTransacao />} />
          {/* O catalogo de regras e leitura para todos os perfis: o
              analista precisa dele para entender o proprio score. A
              gestao das regras chega na Fase 8, com o Supervisor. */}
          <Route path="regras" element={<PaginaDeRegras />} />
        </Route>

        {/* A restricao por perfil e repetida no backend, que e quem decide. */}
        <Route element={<RotaProtegida perfis={['Administrador']} />}>
          <Route path="usuarios" element={<PaginaDeUsuarios />} />
          <Route path="integracoes" element={<PaginaDeIntegracoes />} />
        </Route>

        <Route path="*" element={<PaginaNaoEncontrada />} />
      </Route>
    </Routes>
  );
}
