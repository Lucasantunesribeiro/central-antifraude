# ADR 0018 — O console que um analista usaria, e a demonstração que se explica sozinha

- **Status:** aceito
- **Data:** 2026-09-08
- **Fase:** 13 — Dados de Demonstração e UX Final

## Contexto

Doze fases produziram um sistema tecnicamente completo e uma interface que
existia por obrigação: cada fase acrescentava a tela mínima para provar a sua
parte. O resultado, olhado de uma vez, tinha três problemas que só aparecem
quando alguém abre o produto — e nenhum teste apontaria:

1. **A tela era estreita.** O conteúdo travava em 60 rem. Numa tela de 1568 px
   isso desperdiçava mais de um terço do espaço, e a tabela de fila — que o
   analista olha o dia inteiro — cabia menos linhas do que podia.
2. **Tudo tinha o mesmo peso.** Título, rótulo e número quase do mesmo tamanho:
   nada chamava atenção primeiro, e o número que se veio buscar ficava a meio
   palmo do rótulo que o nomeia.
3. **Os dados da demonstração não existiam no repositório.** Vinham de scripts
   soltos de shell, criados à mão numa sessão anterior. Não eram reproduzíveis,
   não eram idempotentes e ninguém além de quem os escreveu saberia recriá-los.

O `CLAUDE.md` seção 79 já dizia o que a interface deveria parecer — *software
operacional usado por analistas de fraude durante o expediente*, com densidade,
tabelas e leitura rápida. A direção estava definida; faltava executá-la.

---

## Decisão 1 — Barra lateral agrupada por função

Dez telas numa barra horizontal viravam uma fila de links sem hierarquia. Nada
separava *trabalhar a fila de hoje* de *publicar uma versão de regra*.

Três grupos, que são a divisão real do produto e também a ordem em que o
trabalho acontece: **Operação** (painel, alertas, casos, transações), **Risco**
(regras, backtests) e **Governança** (auditoria, integrações, usuários).

A lateral libera a faixa superior e resolve o crescimento: uma tela nova entra
num grupo, e não no fim de uma fila.

---

## Decisão 2 — Largura fluida, com medida de leitura só onde é texto

O conteúdo passou a usar a tela. A exceção é deliberada: `.pagina__resumo` — o
parágrafo que explica a tela — mantém teto de 46 rem, porque linha de 200
caracteres não se lê, se varre.

Tabelas e painéis não têm teto. É o oposto de uma página de conteúdo, e é o
ponto: um console operacional quer mais linhas visíveis, não margens elegantes.

---

## Decisão 3 — Estado nunca depende só de cor

`Permitir`, `Revisar` e `Bloquear` ganharam **forma** além de cor e texto:
círculo, losango e triângulo.

Deuteranopia atinge cerca de um em cada doze homens. Um selo que só muda de
matiz obriga essa pessoa a ler o texto de cada linha, uma por uma, enquanto
todos os outros varrem a coluna de relance. A forma devolve a varredura — e
funciona também numa captura em preto e branco, que é como a maioria das
capturas de tela acaba impressa.

---

## Decisão 4 — O seed pergunta as decisões ao motor

As seis histórias da demonstração estão em `SeedNarrativo`. Ele monta a
transação, monta o contexto histórico e chama o `MotorDeRisco` de verdade, com
a versão de perfil publicada de verdade.

**Um seed que gravasse `score = 75` viraria mentira na primeira alteração de
peso de regra**, e mentira em demonstração é pior do que tela vazia. Do jeito
que está, mexer no catálogo muda os números da demonstração junto — e é isso
que se quer.

O preço é que a narrativa pode quebrar sozinha, e foi o que aconteceu três
vezes durante esta fase (abaixo). Por isso ela tem teste próprio.

**Idempotência** pela presença da primeira transação da história A: um dado real
não pode divergir de si mesmo, e uma tabela de controle poderia.

---

## O que a narrativa ensinou sobre o próprio motor

As três histórias que não saíram como planejadas dizem mais sobre o produto do
que as que saíram:

| Esperado | Medido | O que isso significa |
|---|---|---|
| Divergência geográfica sozinha → alerta | **Permitir** (+25 contra limiar 40) | um sinal isolado não segura ninguém |
| Velocidade sozinha → alerta | **Permitir** (+35) | idem, por outra regra |
| Rajada de quatro em 12 min aciona velocidade | **não acionou** | a janela é de dez minutos, e a mais antiga caía fora |

As duas primeiras viraram conteúdo da demonstração em vez de defeito a
corrigir: a história D existe hoje **para** mostrar que o score é aditivo. Sem
um caso assim, quem vê o produto conclui que todo sinal vira alerta — e passa a
estranhar o dia em que um não virar.

A terceira era erro de quem escreveu o seed, e o teste a pegou.

---

## Decisão 5 — Contraste medido, não escolhido a olho

O tom de texto fraco anterior (`#6b7a8d`) dava **4,15:1** contra a superfície,
abaixo dos 4,5:1 que a WCAG 2.1 exige para texto pequeno — e ele era usado
justamente nos textos menores da interface: rótulo de filtro e cabeçalho de
tabela, ambos com 9,5 px.

Trocado por `#7d8ea3`, que fica entre **4,96 e 5,65:1** nas quatro superfícies
do sistema. Os números foram calculados no navegador, sobre a página real, e não
estimados.

---

## Consequências

**Positivas.** A demonstração cabe num roteiro de oito minutos
([`demonstracao.md`](../demonstracao.md)) e se recria com um comando. A
interface usa a tela, tem hierarquia e continua legível para quem não distingue
as cores. O falso positivo exigido pelo `CLAUDE.md` seção 94 deixou de depender
de um script na máquina de alguém.

**Custo aceito.** Os nomes de classe CSS foram mantidos, embora vários já não
descrevam bem o que estilizam. Uma renomeação em massa faria o diff desta fase
esconder o que de fato mudou — que é aparência, e não estrutura.

**Limite conhecido.** A verificação visual cobriu painel, alertas, casos e
transações em 1456 px. Regras, backtests, auditoria e administração foram
verificadas só pelos testes automatizados e pela herança do sistema de estilos.
Nenhuma tela foi conferida em largura de telefone, e o console não é feito para
telefone — há apenas o mínimo para não quebrar numa janela dividida ao meio.
