import { useState, type FormEvent } from 'react';
import { Navigate, useLocation } from 'react-router';
import { ErroDaApi, mensagemAmigavel } from '../api/erros';
import { EstadoDeCarregamento } from '../componentes/Estados';
import { useSessao } from '../sessao/contextoDeSessao';

interface LocalDeOrigem {
  origem?: string;
}

export function PaginaDeLogin() {
  const sessao = useSessao();
  const local = useLocation();

  const [email, definirEmail] = useState('');
  const [senha, definirSenha] = useState('');
  const [enviando, definirEnviando] = useState(false);
  const [erro, definirErro] = useState<unknown>(null);

  if (sessao.carregando) {
    return <EstadoDeCarregamento rotulo="Restaurando sessao..." />;
  }

  if (sessao.autenticado) {
    // Quem chegou aqui vindo de uma rota protegida volta para ela.
    const destino = (local.state as LocalDeOrigem | null)?.origem ?? '/painel';
    return <Navigate to={destino} replace />;
  }

  async function aoEnviar(evento: FormEvent<HTMLFormElement>) {
    evento.preventDefault();
    definirErro(null);
    definirEnviando(true);

    try {
      await sessao.entrar(email, senha);
    } catch (falha) {
      definirErro(falha);
    } finally {
      definirEnviando(false);
    }
  }

  return (
    <section className="entrada">
      <form
        className="entrada__formulario"
        onSubmit={(e) => void aoEnviar(e)}
        noValidate
      >
        <h1>Entrar</h1>
        <p className="entrada__resumo">Central Antifraude</p>

        <label className="campo" htmlFor="email">
          <span className="campo__rotulo">E-mail</span>
          <input
            id="email"
            name="email"
            type="email"
            autoComplete="username"
            required
            value={email}
            onChange={(e) => definirEmail(e.target.value)}
            disabled={enviando}
          />
        </label>

        <label className="campo" htmlFor="senha">
          <span className="campo__rotulo">Senha</span>
          <input
            id="senha"
            name="senha"
            type="password"
            autoComplete="current-password"
            required
            value={senha}
            onChange={(e) => definirSenha(e.target.value)}
            disabled={enviando}
          />
        </label>

        {erro !== null ? (
          // role="alert" faz o leitor de tela anunciar a falha sem que o
          // usuario precise procurar a mensagem na tela.
          <p className="entrada__erro" role="alert">
            {mensagemAmigavel(erro)}
            {erro instanceof ErroDaApi && erro.idDeCorrelacao ? (
              <>
                {' '}
                <span className="entrada__correlacao">
                  Codigo: <code>{erro.idDeCorrelacao}</code>
                </span>
              </>
            ) : null}
          </p>
        ) : null}

        <button className="botao botao--principal" type="submit" disabled={enviando}>
          {enviando ? 'Entrando...' : 'Entrar'}
        </button>
      </form>
    </section>
  );
}
