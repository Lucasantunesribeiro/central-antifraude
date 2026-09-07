import { Route, Routes } from 'react-router';
import { LayoutDoApp } from '../layout/LayoutDoApp';
import { PaginaDeLogin } from '../paginas/PaginaDeLogin';
import { PaginaDeAlertas } from '../paginas/PaginaDeAlertas';
import { PaginaDeAuditoria } from '../paginas/PaginaDeAuditoria';
import { PaginaDeBacktests } from '../paginas/PaginaDeBacktests';
import { PaginaDoBacktest } from '../paginas/PaginaDoBacktest';
import { PaginaDeCasos } from '../paginas/PaginaDeCasos';
import { PaginaDoCaso } from '../paginas/PaginaDoCaso';
import { PaginaDeIntegracoes } from '../paginas/PaginaDeIntegracoes';
import { PaginaDaRegra } from '../paginas/PaginaDaRegra';
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
          {/* A fila operacional e leitura para todo perfil autenticado,
              inclusive o Auditor: consultar decisoes e o trabalho dele. Agir
              sobre um alerta e acao de caso, e casos sao a Fase 7. */}
          <Route path="alertas" element={<PaginaDeAlertas />} />
          {/* Casos sao leitura para todo perfil, inclusive o Auditor. Agir
              sobre um caso e outra coisa, e quem recusa e a API. */}
          <Route path="casos" element={<PaginaDeCasos />} />
          <Route path="casos/:id" element={<PaginaDoCaso />} />
          {/* O catalogo de regras e leitura para todos os perfis: o
              analista precisa dele para entender o proprio score. A mesma
              tela vira administracao para a supervisao — e quem recusa a
              escrita e a API, nao a tela. */}
          <Route path="regras" element={<PaginaDeRegras />} />
        </Route>

        {/* O detalhe da regra so faz sentido para quem administra: ele e
            rascunho, publicacao e historico. O backend responde 403 a
            qualquer outro perfil. */}
        <Route
          element={<RotaProtegida perfis={['Administrador', 'SupervisorDeFraude']} />}
        >
          <Route path="regras/:id" element={<PaginaDaRegra />} />
          {/* Backtest e supervisao inteira, leitura inclusive: e um ensaio
              sobre uma decisao que ainda nao foi tomada, e nao uma decisao. O
              material do Auditor sao as decisoes reais e a trilha. */}
          <Route path="backtests" element={<PaginaDeBacktests />} />
          <Route path="backtests/:id" element={<PaginaDoBacktest />} />
        </Route>

        {/* A trilha e um controle SOBRE quem opera: dar a quem e auditado o
            poder de varrer o proprio rastro enfraquece o unico registro que
            responde "quem fez o que e quando". O backend recusa os outros
            perfis com 403. */}
        <Route element={<RotaProtegida perfis={['Administrador', 'Auditor']} />}>
          <Route path="auditoria" element={<PaginaDeAuditoria />} />
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
