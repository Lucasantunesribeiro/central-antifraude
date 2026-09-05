# ADR 0011 — Alertas operacionais

- **Status:** aceito
- **Data:** 2026-09-05
- **Fase:** 6 — Alertas Operacionais

## Contexto

A Fase 5 fechou o caminho assíncrono com um efeito deliberadamente modesto: um
contador diário por decisão. Esta fase troca o efeito por outro que **uma
pessoa vê**: a fila de trabalho do analista.

A diferença não é de tamanho, é de consequência. Um contador errado é um número
errado num painel. Um alerta duplicado é um analista investigando duas vezes o
mesmo caso — e concluindo duas vezes.

---

## Decisão 1 — Um consumidor a mais, e não uma fila a mais

O mesmo evento `TransacaoAvaliada.v1` agora precisa produzir **dois efeitos**:
a projeção diária e o alerta. Isso é fan-out, e havia duas formas.

| Alternativa | Por que não / por que sim |
|---|---|
| Publicar em duas filas, uma por consumidor | É o que se faz quando os consumidores rodam em processos separados; no SQS exige um tópico na frente. Dá duas chances de divergir e nenhuma vantagem aqui |
| **Um processo lê uma vez e aplica os dois efeitos na mesma transação** | Escolhida. A Central Antifraude é um monólito modular, os dois efeitos gravam no mesmo banco e nenhum chama serviço externo |

A abstração é `IManipuladorDeEvento`: nome do consumidor e um método que aplica
o efeito. **Cada manipulador tem sua própria marca na Inbox** — a chave é
`(consumidor, evento)`, e não só o evento. Isso não é decoração: é o que
permite, na Fase 14, mover um dos dois para outro processo sem mudar nada da
semântica de idempotência.

O ganho já apareceu: a Inbox da Fase 5 foi desenhada com a chave composta
justamente para este momento, e nada precisou mudar nela.

---

## Decisão 2 — Um savepoint por efeito

```text
BEGIN
  para cada manipulador:
    SAVEPOINT
    grava a Inbox        ← a restrição única decide
    aplica o efeito
COMMIT
apaga da fila
```

O savepoint é a decisão técnica central da fase, e ela vem de uma propriedade
do PostgreSQL: **um erro aborta a transação inteira**. Sem savepoint, o
conflito da marca de um efeito já aplicado mataria também o efeito que ainda
não tinha acontecido — e essa combinação não é rara, é o estado normal depois
de o processo cair no meio de uma entrega.

O mesmo `catch` cobre as duas camadas de idempotência: a marca da Inbox e a
restrição própria do efeito. A conclusão é a mesma nos dois casos — *isto já
existe, e não pode existir duas vezes*.

Verificado com teste: apagar a marca de um dos consumidores e reentregar o
evento faz o outro conflitar, e o primeiro ainda assim acontece.

---

## Decisão 3 — Duas camadas de idempotência, e as duas fazem falta

O `CLAUDE.md` seção 42 pede idempotência em mais de uma camada *quando o
domínio justificar*, e cita literalmente `Alert.EvaluationId UNIQUE`. Aqui o
domínio justifica.

| Camada | O que ela impede |
|---|---|
| Inbox `(consumidor, evento)` | O mesmo evento ser processado duas vezes por este consumidor |
| `alertas.avaliacao_id` **único** | Um segundo alerta para a mesma avaliação, venha de onde vier |

Dois testes derrubam **uma camada de cada vez** para mostrar que a outra
sozinha já segura. Sem eles, seria impossível saber qual das duas está
realmente trabalhando — e uma proteção que nunca foi exercitada é uma proteção
que ninguém sabe se existe.

---

## Decisão 4 — A política é código, versionada, e não é um segundo motor

```text
Permitir → nenhum alerta
Revisar  → alerta, prioridade Média
Bloquear → alerta, prioridade Alta
```

`PoliticaDeAlertas` **não lê histórico, não consulta banco, não soma pontos e
não reavalia nada**. Ela recebe a decisão que o motor já tomou e responde uma
pergunta operacional: isto precisa de olho humano, e com que urgência?

Reavaliar aqui seria pior do que redundante. O consumidor roda depois, em outro
processo, com o catálogo de regras possivelmente já republicado — chegaria a um
resultado diferente do que o integrador recebeu na resposta síncrona, e o
painel passaria a discordar do recibo.

`Permitir` não gera alerta de propósito: uma fila que recebesse toda transação
permitida deixaria de ser fila de trabalho e viraria um espelho da tabela de
transações — que já existe, na tela de Transações.

**A versão da política fica gravada em cada alerta**, pelo mesmo motivo que a
avaliação guarda a versão do perfil: para que *"por que este alerta é Alta?"*
continue tendo resposta depois que a política mudar.

Os valores são **configuração de demonstração**, não prática de mercado
(`CLAUDE.md` seção 112). O que existe de fundamentado é a tripla
permitir/revisar/bloquear, documentada na ADR 0008.

---

## Decisão 5 — O alerta copia o que a fila filtra, e referencia o resto

| Campo | Origem | Por quê |
|---|---|---|
| `score`, `decisao` | Cópia da avaliação | A fila filtra e ordena por eles; buscar em outra tabela obrigaria junção na consulta mais usada do produto |
| Sinais | Referência (`avaliacao_id`) | Já existem, com explicação e versão de regra; duplicá-los seria denormalização sem pergunta que a justifique |
| `evento_id`, `id_de_correlacao` | Procedência | O último elo da corrente da seção 69 |

A cópia é segura porque **a avaliação é congelada** — a Fase 4 provou com
teste que ela não muda depois de gravada. Copiar um valor imutável não cria uma
segunda fonte de verdade; copiar um valor mutável criaria.

Os sinais da página inteira vêm em **uma consulta em lote**, não uma por
alerta. A tela mostra os três de maior peso e o total, para que três sinais
numa avaliação de cinco não façam o analista acreditar que viu tudo.

---

## Decisão 6 — A fila é somente leitura nesta fase

Não existe rota que crie, altere, atribua, feche ou apague alerta — e o
Security Gate 6 **verifica a ausência**, com sete métodos e caminhos.

Alerta é resultado de avaliação, produzido pelo consumidor a partir de um
evento que o próprio sistema publicou. Abrir escrita agora criaria um caminho
para inventar trabalho de investigação a partir de um JSON: sem avaliação, sem
sinais e sem explicação.

A ação humana sobre um alerta — assumir, investigar, resolver — pertence ao
**caso**, que é a Fase 7. Até lá `StatusDoAlerta` tem um valor só, `Aberto`, e
isso é deliberado: o `CLAUDE.md` seção 12 pede estado novo apenas com
necessidade operacional clara.

Também não há rota de detalhe. O ROADMAP 6.6 pede navegação do alerta para a
transação, a avaliação e os sinais — e essa tela existe desde a Fase 3. Cada
linha da fila carrega o `transacaoId`, e o caminho termina lá.

---

## Decisão 7 — Filtro desconhecido é recusado, nunca ignorado

Decisão e prioridade são resolvidas contra os **nomes** do enum, e não por
`Enum.TryParse`. A diferença importa: `TryParse` sozinho aceitaria
`?decisao=2` como `Revisar` e deixaria `?prioridade=99` atravessar como um
valor que não existe.

E o filtro inválido produz `400`, não uma lista sem filtro. Descartar em
silêncio devolveria a fila inteira, e o analista acreditaria estar vendo só os
bloqueios. Numa fila de fraude, **acreditar que se filtrou é pior do que
receber um erro** — a mesma decisão que a paginação já tomava desde a Fase 0 ao
recusar um pedido de 5.000 registros em vez de reduzi-lo.

---

## Decisão 8 — A ordenação por prioridade não usa a coluna

A prioridade é gravada como **texto**, para que uma consulta manual durante uma
investigação diga `Alta` e não `2`. Mas texto ordena em ordem alfabética — e
aqui o alfabeto é o **inverso** da gravidade: `"Alta"` vem antes de `"Media"`.

Ordenar pela coluna direto poria os alertas menos graves no topo quando o
analista pedisse "mais grave primeiro", **e nada na tela denunciaria o
engano** — a lista pareceria perfeitamente ordenada.

Por isso a ordem por prioridade vem de uma expressão explícita de gravidade, e
não da coluna. O custo aceito: acrescentar um nível de prioridade exige alterar
essa expressão, e o compilador não avisa. O teste de integração avisa.

---

## Consequências

**Ganhos**

- O primeiro efeito assíncrono que uma pessoa consome, com idempotência provada
  em duas camadas independentes.
- A abstração de manipulador prepara a Fase 14 sem custo hoje.
- Correlação atravessa HTTP → domínio → Outbox → mensagem → worker → **alerta**.
- Filtro, ordenação e paginação no servidor, com vocabulário fechado.

**Custos aceitos**

- Os dois efeitos compartilham transação e processo. Separá-los exigirá duas
  filas — e a Inbox já está preparada para isso.
- O alerta duplica `score` e `decisao`. Seguro enquanto a avaliação for
  imutável; se um dia deixar de ser, esta decisão precisa ser reaberta.
- A ordenação por prioridade depende de uma expressão mantida à mão.

**Dívida registrada**

- Sem tela de detalhe do alerta. A navegação vai para a transação, e é onde a
  explicação completa mora.
- Sem métrica exportada de tamanho da fila e idade do alerta mais antigo —
  Fase 12. O contador `alertas_criados` existe, com dimensão de prioridade.
- Sem ação humana sobre o alerta. Fase 7.
