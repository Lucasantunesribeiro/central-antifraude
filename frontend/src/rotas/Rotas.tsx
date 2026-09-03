import { Route, Routes } from 'react-router';
import { LayoutDoApp } from '../layout/LayoutDoApp';
import { PaginaDoConsole } from '../paginas/PaginaDoConsole';
import { PaginaInicial } from '../paginas/PaginaInicial';
import { PaginaNaoEncontrada } from '../paginas/PaginaNaoEncontrada';
import { RotaProtegida } from './RotaProtegida';

export function Rotas() {
  return (
    <Routes>
      <Route element={<LayoutDoApp />}>
        <Route index element={<PaginaInicial />} />

        <Route element={<RotaProtegida />}>
          <Route path="console" element={<PaginaDoConsole />} />
        </Route>

        <Route path="*" element={<PaginaNaoEncontrada />} />
      </Route>
    </Routes>
  );
}
