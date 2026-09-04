import { Navigate, Outlet, useLocation } from 'react-router';
import { EstadoDeCarregamento } from '../componentes/Estados';
import { useSessao } from '../sessao/contextoDeSessao';
import type { PerfilDeUsuario } from '../api/tipos';

/**
 * Guarda de rota do lado do cliente.
 *
 * Vale repetir o que o CLAUDE.md secao 52 diz: isto e conveniencia de
 * navegacao, NAO seguranca. Quem autoriza e o backend, que responde 403 a
 * qualquer chamada fora do perfil. Esconder uma rota no navegador nao impede
 * ninguem de chamar a API direto.
 *
 * O estado de carregamento importa: sem ele, um F5 jogaria o usuario para a
 * tela de login antes de a sessao ser restaurada pelo cookie.
 */
export function RotaProtegida({ perfis }: { perfis?: readonly PerfilDeUsuario[] }) {
  const sessao = useSessao();
  const local = useLocation();

  if (sessao.carregando) {
    return <EstadoDeCarregamento rotulo="Restaurando sessao..." />;
  }

  if (!sessao.autenticado) {
    return <Navigate to="/entrar" replace state={{ origem: local.pathname }} />;
  }

  if (perfis && !perfis.includes(sessao.usuario!.perfil)) {
    return <Navigate to="/painel" replace />;
  }

  return <Outlet />;
}
