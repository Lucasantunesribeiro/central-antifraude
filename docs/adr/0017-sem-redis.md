# ADR 0017 — PostgreSQL continua suficiente: sem Redis

- **Status:** aceito
- **Data:** 2026-09-07
- **Fase:** 12 — Observabilidade, Resiliência e Performance
- **Responde ao:** `ROADMAP.md` 12.9 (checkpoint explícito)

## O checkpoint

O `ROADMAP.md` obriga esta fase a fazer uma pergunta, e só permite respondê-la
**depois** de medir:

> Os benchmarks demonstraram necessidade concreta de cache/distributed state?

Se não: documentar que o PostgreSQL continua suficiente. Se sim: **parar** — a
decisão reabriria o `CLAUDE.md` seções 44 e 45, e isso não é decisão de fase.

## Resposta

**Não.** As medições estão em
[`baseline-de-performance.md`](../baseline-de-performance.md) e nenhuma delas
descreve um problema que um cache resolveria.

### O que foi medido

| Cenário | Resultado |
|---|---|
| Ingestão sequencial | p50 **19 ms**, p99 abaixo de 1 s |
| 8 clientes independentes, 64 transações | 63 aceitas, 1 em contenção, **56/s** |
| 20 transações simultâneas do mesmo cliente | todas resolvidas, nenhuma inconsistente |
| 25 repetições simultâneas da mesma requisição | 1 transação, 1 avaliação, **156/s** |
| 60 eventos represados | drenados em 6 ciclos, sem perda nem duplicata |

O envelope de portfólio do `CLAUDE.md` seção 77 é de **~10.000 avaliações por
mês** — cerca de 4 por hora. O caminho crítico medido está três ordens de
grandeza acima disso.

### As consultas críticas usam índice

`EXPLAIN` confirma varredura por índice nas três consultas que rodam sempre:
a janela histórica do cliente, a primeira página do console e a leitura da
Outbox pendente. Nenhuma delas varre tabela, e nenhuma tem custo que cresça com
o total de dados do tenant.

**Não há consulta lenta para esconder atrás de um cache.**

## O único número desconfortável, e por que Redis não o resolve

Oito clientes **independentes** produziram uma contenção de serialização. Eles
não compartilham dado nenhum: a janela histórica de um não inclui as transações
do outro.

A causa está no plano de execução, e não no domínio. Sob `SERIALIZABLE`, o
PostgreSQL toma predicado sobre o que a consulta **lê** — e uma varredura
sequencial lê a relação inteira, então o predicado cobre a tabela toda e todos
passam a conflitar com todos. Com a tabela `transacoes` pequena, varrer é
legitimamente mais barato do que abrir índice, e o planejador escolhe varrer.

Isso importa para esta decisão por um motivo específico: **é um problema de
isolamento, e não de latência de leitura.** Um cache não muda o predicado que o
PostgreSQL toma dentro da transação crítica. As três respostas possíveis são:

1. **volume** — com dados, o planejador escolhe o índice e o predicado encolhe
   para as linhas daquele cliente. Verificado em `DesempenhoDeConsultasTests`;
2. **a mitigação que já existe** — `SET LOCAL cpu_tuple_cost` na operação
   crítica, decidida na Fase 4, que reduz o efeito enquanto o volume é baixo;
3. **retry deliberado** — que já existe, devolve `503` com `Retry-After` e é
   seguro repetir com a mesma chave de idempotência.

Nenhuma delas é Redis.

## O que faria reabrir a decisão

Registrado para que a próxima pessoa não precise adivinhar. Só um destes:

- uma consulta do caminho crítico cujo custo **não** possa ser resolvido com
  índice, modelagem ou reescrita — e medida, não suposta;
- necessidade de estado **compartilhado entre instâncias**. Hoje existe uma:
  o rate limiting é por processo, e com várias instâncias em Lambda o limite
  efetivo se multiplica. Isso está registrado como limitação conhecida desde a
  Fase 11 e não é, por si, motivo suficiente — o limite existe contra abuso
  grosseiro, e um teto três vezes maior continua sendo um teto;
- volume de leitura que torne o Neon o gargalo, com número medido ao lado.

Se algum dia for reaberta, o `CLAUDE.md` seção 45 exige documentar: o dado
cacheado, TTL, estratégia de invalidação, comportamento em *cache miss*, fonte
de verdade e impacto da inconsistência.

## Consequência

A arquitetura da Fase 14 não precisa de ElastiCache, e o custo esperado de
US$ 0,00 do `CLAUDE.md` seção 76 continua alcançável — ElastiCache não tem
camada gratuita permanente, e seria o primeiro custo fixo do projeto.
