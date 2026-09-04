import { Route, Routes } from 'react-router';
import { LayoutDoApp } from '../layout/LayoutDoApp';
import { PaginaDeLogin } from '../paginas/PaginaDeLogin';
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
        </Route>

        {/* A restricao por perfil e repetida no backend, que e quem decide. */}
        <Route element={<RotaProtegida perfis={['Administrador']} />}>
          <Route path="usuarios" element={<PaginaDeUsuarios />} />
        </Route>

        <Route path="*" element={<PaginaNaoEncontrada />} />
      </Route>
    </Routes>
  );
}
