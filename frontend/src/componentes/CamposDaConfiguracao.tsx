import { foraDaFaixa, type CampoDeConfiguracao } from '../api/regras';

/**
 * Formulário de configuração montado a partir do contrato do servidor.
 *
 * **A tela não conhece nenhuma regra.** Rótulo, faixa, valor padrão e se o
 * campo aceita casa decimal vêm de `/api/regras/tipos`. Repetir isso aqui
 * criaria uma segunda lista de limites que sairia de sincronia no primeiro
 * ajuste do backend — e a tela passaria a aceitar o que o domínio recusa.
 *
 * O aviso de faixa é conveniência de digitação, não autoridade: quem recusa é
 * o backend, com a mesma faixa (CLAUDE.md seção 81).
 */
export function CamposDaConfiguracao({
  campos,
  valores,
  aoMudar,
}: {
  campos: readonly CampoDeConfiguracao[];
  valores: Record<string, number>;
  aoMudar: (valores: Record<string, number>) => void;
}) {
  return (
    <div className="formulario-em-linha">
      {campos.map((campo) => {
        const valor = valores[campo.nome] ?? campo.padrao;
        const invalido = foraDaFaixa(campo, valor);

        // Rotulo por `htmlFor`, e nao envolvendo o campo: o texto de ajuda
        // fica dentro do bloco mas fora do rotulo, para que o nome acessivel
        // do campo seja o rotulo e nao "Minimo de transacoes... De 1 a 100".
        const identificador = `campo-${campo.nome}`;

        return (
          <div className="campo" key={campo.nome}>
            <label className="campo__rotulo" htmlFor={identificador}>
              {campo.rotulo}
            </label>
            <input
              id={identificador}
              type="number"
              value={valor}
              min={campo.minimo}
              max={campo.maximo}
              step={campo.tipo === 'Inteiro' ? 1 : 0.1}
              aria-invalid={invalido}
              onChange={(evento) =>
                aoMudar({ ...valores, [campo.nome]: Number(evento.target.value) })
              }
            />
            <span className="campo__ajuda">
              De {campo.minimo} a {campo.maximo}
              {campo.tipo === 'Inteiro' ? ', número inteiro' : ''}
            </span>
          </div>
        );
      })}
    </div>
  );
}
