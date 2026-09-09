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
  const [entrandoDemo, definirEntrandoDemo] = useState(false);
  const [erro, definirErro] = useState<unknown>(null);

  if (sessao.carregando) {
    return <EstadoDeCarregamento rotulo="Restaurando sessao..." />;
  }

  if (sessao.autenticado) {
    // Quem chegou aqui vindo de uma rota protegida volta para ela.
    const destino = (local.state as LocalDeOrigem | null)?.origem ?? '/painel';
    return <Navigate to={destino} replace />;
  }

  const ocupado = enviando || entrandoDemo;

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

  async function aoEntrarComoDemo() {
    definirErro(null);
    definirEntrandoDemo(true);

    try {
      await sessao.entrarComoDemo();
    } catch (falha) {
      definirErro(falha);
    } finally {
      definirEntrandoDemo(false);
    }
  }

  return (
    <section className="entrada">
      <div className="entrada__cartao">
        <p className="entrada__marca">Central Antifraude</p>

        <form
          className="entrada__formulario"
          onSubmit={(e) => void aoEnviar(e)}
          noValidate
        >
          <h1>Entrar</h1>
          <p className="entrada__resumo">
            Avaliacao de risco, monitoramento e investigacao de transacoes.
          </p>

          {/*
            O acesso de demonstracao vem PRIMEIRO e em destaque: quem chega ao
            site quase sempre so quer olhar, e nao tem uma conta. Pedir e-mail e
            senha antes de deixar entrar e a barreira que fazia o site parecer
            fechado.
          */}
          <button
            type="button"
            className="botao botao--principal entrada__demo"
            onClick={() => void aoEntrarComoDemo()}
            disabled={ocupado}
          >
            {entrandoDemo ? 'Entrando...' : 'Explorar a demonstracao →'}
          </button>

          <p className="entrada__ou">ou entre com sua conta</p>

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
              disabled={ocupado}
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
              disabled={ocupado}
            />
          </label>

          {erro !== null ? (
            // role="alert" faz o leitor de tela anunciar a falha sem que o
            // usuario precise procurar a mensagem na tela.
            <p className="entrada__erro" role="alert">
              {mensagemAmigavel(erro)}
              {erro instanceof ErroDaApi && erro.idDeCorrelacao ? (
                <span className="entrada__correlacao">
                  Codigo: <code>{erro.idDeCorrelacao}</code>
                </span>
              ) : null}
            </p>
          ) : null}

          <button className="botao entrada__demo" type="submit" disabled={ocupado}>
            {enviando ? 'Entrando...' : 'Entrar'}
          </button>
        </form>

        <p className="entrada__rodape">
          Dados ficticios por construcao. Nenhuma informacao real de pagamento e
          armazenada.
        </p>
      </div>
    </section>
  );
}
