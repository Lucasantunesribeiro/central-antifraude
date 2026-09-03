import { Navigate, Outlet, useLocation } from 'react-router';
import { EstadoDeCarregamento } from '../componentes/Estados';
import { useSessao } from '../sessao/useSessao';

/**
 * Guarda de rota do lado do cliente.
 *
 * Vale repetir o que o CLAUDE.md secao 52 diz: isto e conveniencia de
 * navegacao, NAO seguranca. Quem autoriza e o backend. Esconder uma rota no
 * navegador nao impede ninguem de chamar a API direto.
 *
 * Na Fase 0 nao ha login, entao nenhuma rota protegida e acessivel ainda.
 */
export function RotaProtegida() {
  const sessao = useSessao();
  const local = useLocation();

  if (sessao.carregando) {
    return <EstadoDeCarregamento rotulo="Restaurando sessao..." />;
  }

  if (!sessao.autenticado) {
    return <Navigate to="/" replace state={{ origem: local.pathname }} />;
  }

  return <Outlet />;
}
