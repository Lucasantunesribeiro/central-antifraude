import { Component, type ErrorInfo, type ReactNode } from 'react';
import { EstadoDeErro } from './Estados';

interface Props {
  children: ReactNode;
}

interface Estado {
  erro: unknown;
}

/**
 * Ultima barreira da interface.
 *
 * Sem ela, uma excecao de renderizacao numa celula de tabela apaga a tela
 * inteira e deixa o analista olhando para uma pagina branca no meio de uma
 * investigacao. Com ela, o erro fica contido e o resto do console continua.
 */
export class LimiteDeErro extends Component<Props, Estado> {
  constructor(props: Props) {
    super(props);
    this.state = { erro: null };
  }

  static getDerivedStateFromError(erro: unknown): Estado {
    return { erro };
  }

  override componentDidCatch(erro: Error, informacao: ErrorInfo): void {
    // Sem servico de telemetria ainda (Fase 12). O console do navegador e o
    // destino honesto por enquanto.
    console.error('Falha de renderizacao', erro, informacao.componentStack);
  }

  override render(): ReactNode {
    if (this.state.erro) {
      return (
        <EstadoDeErro
          erro={this.state.erro}
          aoTentarDeNovo={() => this.setState({ erro: null })}
        />
      );
    }

    return this.props.children;
  }
}
