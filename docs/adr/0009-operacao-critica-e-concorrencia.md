# ADR 0009 — Operação crítica, concorrência e Outbox

- **Status:** aceito
- **Data:** 2026-09-04
- **Fase:** 4 — Avaliação Síncrona e Concorrência

## Contexto

Até a Fase 3 a ingestão já registrava a transação, avaliava o risco e devolvia
a decisão — mas cada uma dessas etapas confiava apenas em restrições únicas do
banco. Isso resolve **duplicidade**; não resolve **leitura que deixa de ser
verdadeira**.

O `CLAUDE.md` seção 35 nomeia o problema:

> Regras de velocidade e contexto histórico podem ser afetadas por transações
> simultâneas.

E a seção 36 exige que o retry seja deliberado: rollback, releitura, nova
avaliação — nunca reexecutar só o comando SQL que falhou.

Esta fase fecha isso e acrescenta a Outbox como modelo local, para que a Fase 5
publique sem mudar o esquema.

---

## Decisão 1 — Boundary transacional explícito, e só na ingestão

`IExecutorDeOperacaoCritica` abre uma transação **`SERIALIZABLE`**, roda a
operação e faz o commit dentro do bloco protegido pelo retry.

| Onde | Isolamento |
|---|---|
| `POST /api/ingestao/transacoes` | `SERIALIZABLE`, com retry |
| Login, refresh, usuários, integrações, consultas | padrão (`READ COMMITTED`) |

**Por que não em todo endpoint.** Nenhuma das outras operações depende de um
conjunto lido para estar correta: elas dependem de linhas identificadas, e uma
restrição única já basta. Ligar isolamento forte ali só traria retry onde não
há conflito (ROADMAP 4.3).

**Por que a validação fica fora da transação.** Ela é determinística e não toca
no banco. Abrir uma transação serializável para recusar um payload malformado
gastaria isolamento forte com quem nem chega a competir por nada.

### Por que `SERIALIZABLE` e não uma trava

A avaliação lê um **conjunto** — "quantas transações deste cliente nos últimos
10 minutos" — e decide a partir dele. Não há uma linha única para travar: o
risco é uma leitura que deixa de ser verdadeira porque alguém **inseriu** algo
que ela deveria ter visto. O PostgreSQL detecta exatamente isso e aborta uma
das transações com SQLSTATE `40001`.

A alternativa seria um advisory lock por cliente. Funcionaria, mas serializaria
por cliente mesmo sem conflito, e trocaria uma detecção que o banco já faz por
uma trava que todo caminho novo precisaria lembrar de pegar.

---

## Decisão 2 — O retry é da operação inteira

Em conflito reconhecido: a transação é abortada, o `ChangeTracker` é limpo, e a
operação roda **do zero** — releitura do contexto, nova avaliação, nova
gravação.

Limpar o rastreamento não é detalhe: as entidades da tentativa abortada
continuariam marcadas como `Added`, e a releitura devolveria instâncias que
nunca foram gravadas.

### O que é refeito, e o que não é

| Condição | Refaz? | Por quê |
|---|---|---|
| `40001` serialization failure | ✅ | Não existe ordem serial válida; uma precisa recomeçar |
| `40P01` deadlock | ✅ | Mesma resposta: abortar e refazer |
| Violação de unicidade | ✅ | Outra requisição venceu a corrida (ver Decisão 3) |
| Validação, conflito de negócio, autenticação, autorização | ❌ | Repetir daria o mesmo resultado, quatro vezes mais devagar (`CLAUDE.md` 37) |

A espera entre tentativas é crescente **com sorteio**. O sorteio importa mais
que o crescimento: duas requisições que esperassem o mesmo tempo voltariam a
colidir na mesma janela.

---

## Decisão 3 — A terceira camada de idempotência muda de forma

Na Fase 2, quem perdia o `INSERT` reconsultava e encontrava a vencedora. Sob
`SERIALIZABLE` **isso não funciona**: o snapshot da transação é anterior ao
commit da outra, e a linha da vencedora é invisível para ela.

A terceira camada passa a ser o **retry da operação inteira**, com snapshot
novo — no qual a camada 1 encontra a vencedora e devolve o resultado dela.

```text
Fase 2 (READ COMMITTED)          Fase 4 (SERIALIZABLE)
1. consulta                      1. consulta
2. constraint única              2. constraint única
3. reconsulta na mesma transação  3. refaz a operação com snapshot novo
```

Verificado: 20 requisições simultâneas idênticas produzem **1× 201, 19× 200,
uma transação, uma avaliação e um evento**.

---

## Decisão 4 — Contenção esgotada é 503, não 500

Quando o orçamento de tentativas acaba, a API devolve **503 com
`Retry-After: 1`** e o código `contencao_de_concorrencia`.

500 diria "algo quebrou, não insista". 503 diz "estava disputado, repita" — e
repetir com a mesma chave de idempotência é seguro por construção: ou a
transação já existe e o cliente recebe a avaliação original, ou ela não existe e
será criada uma vez só.

Para um integrador com retry automático, essa diferença decide se ele repete ou
desiste.

---

## Decisão 5 — Varredura sequencial é desencorajada dentro da transação crítica

**Este é o achado técnico da fase, e ele foi medido, não suposto.**

A primeira versão falhava com **doze requisições simultâneas de doze clientes
diferentes** — que não compartilham dado nenhum. O log mostrou uma tempestade
de `40001` puro: 28 retentativas e duas operações esgotando o orçamento.

A causa está na documentação do PostgreSQL (*Transaction Isolation, Serializable
Isolation Level*, consultada em 2026-09-04), literalmente:

> "A sequential scan will always necessitate a relation-level predicate lock.
> This can result in an increased rate of serialization failures. It may be
> helpful to encourage the use of index scans by reducing `random_page_cost`
> and/or increasing `cpu_tuple_cost`."

Com a tabela pequena, o planejador escolhe varredura sequencial — e o bloqueio
de predicado deixa de cobrir a faixa de um cliente e passa a cobrir a tabela
inteira. Aí qualquer inserção conflita com qualquer leitura.

A transação crítica passa a executar, e **somente ela**:

```sql
SET LOCAL cpu_tuple_cost = 1.0
```

`SET LOCAL` vale até o fim da transação e não muda o planejamento de nenhuma
outra consulta do sistema. Verificado com `EXPLAIN`: a consulta de contexto
passa a usar `ix_transacoes_organizacao_id_cliente_externo_id_ocorrida_em`.

O valor é configuração validada em faixa fechada, e nunca vem de entrada
externa — é por isso que a supressão de `EF1002`/`EF1003` naquele ponto é
segura, e não porque "ali não tem usuário".

### O orçamento de tentativas: 8, e o número tem origem

| Tentativas | Resultado medido (12 requisições simultâneas, tabela pequena) |
|---|---|
| 4 | 10 criadas, **2 respondidas com 503** |
| 8 | **12 criadas**, 28 retentativas no total |

Ainda é um limite pequeno (`CLAUDE.md` 36): o pior caso soma esperas de 10 a
200 ms sorteadas, abaixo de um segundo. Se oito execuções seguidas conflitam, o
problema não é azar — é contenção real, que insistir só piora.

**Limitação registrada com honestidade:** o conflito falso encolhe conforme a
tabela cresce e o índice fica seletivo. Em uma organização recém-criada, com
poucas transações, uma rajada simultânea ainda pode gastar retentativas. O
comportamento é correto em qualquer caso — no pior deles, 503 com `Retry-After`.

---

## Decisão 6 — A semântica de concorrência é "equivalente a alguma ordem serial"

O que o sistema promete, e o que **não** promete:

- ✅ o resultado equivale a **alguma** ordem serial válida das operações
  simultâneas;
- ❌ não se promete que toda requisição enxergue as simultâneas. Isso seria
  exigir que ela enxergasse o futuro.

O teste que verifica isso é exato. Seis transações do mesmo cliente, com o
**mesmo `OccurredAt`**, enviadas ao mesmo tempo. Como a janela da regra inclui
tudo com `OccurredAt <=` o da avaliada, em qualquer ordem serial válida a
k-ésima vê k−1 anteriores. Logo as contagens registradas têm que ser
exatamente **{4, 5, 6}**, com três avaliações abaixo do limite.

Uma contagem repetida significaria duas transações que não enxergaram uma à
outra — leitura perdida, sem ordem serial que explicasse o resultado. Um buraco
significaria transação sumida do contexto.

---

## Decisão 7 — Transactional Outbox como modelo local

A linha de evento entra na **mesma transação** da avaliação:

```text
BEGIN (SERIALIZABLE)
  transação
  avaliação + sinais
  evento de saída
COMMIT
```

O que isso elimina e o que não elimina:

| Cenário | Sem Outbox | Com Outbox |
|---|---|---|
| Publica e não grava | consumidor age sobre decisão que não existe | impossível |
| Grava e não publica | efeito nunca acontece | vira atraso; o despachante retoma |
| Publica e cai antes de marcar | — | mensagem repetida — **esperado** (`CLAUDE.md` 40) |

A entrega é **at-least-once**. A correção do terceiro caso pertence ao
consumidor idempotente da Fase 5, não a uma promessa de exatamente-uma-vez que
ninguém consegue cumprir.

**O replay não gera um segundo evento.** O fato "esta transação foi avaliada"
aconteceu uma vez só; publicá-lo a cada retry do integrador criaria um alerta
por tentativa em vez de um por transação.

### O conteúdo é mínimo

`TransacaoAvaliada.v1` carrega identificadores, valor, os três tempos, score,
decisão e os sinais. **Não** carrega referência de instrumento, fingerprint de
dispositivo nem fingerprint de IP — quem precisar deles vai à transação, dentro
do tenant, com autorização (`CLAUDE.md` 56). Há teste que varre o `jsonb`
gravado procurando exatamente esses valores.

O tipo é versionado e a leitura passa por um `switch` fechado: um tipo
desconhecido falha alto, do mesmo jeito que a configuração de regra.

---

## Decisão 8 — Os três tempos, e o que um evento atrasado pode e não pode

| Campo | Origem | Quem crava |
|---|---|---|
| `OccurredAt` | sistema de origem | a integração informa |
| `ReceivedAt` | chegada | o relógio do servidor |
| `EvaluatedAt` | conclusão da avaliação | o relógio do servidor |

Uma transação atrasada é avaliada com **o contexto dela** — o passado que
existia no momento em que ela aconteceu. E as avaliações anteriores **não são
recalculadas**: o sistema não volta atrás para recontar a janela delas agora que
soube de um evento mais antigo (`CLAUDE.md` 16).

Reescrever uma decisão já dada faz a trilha de auditoria mentir: o registro
passaria a dizer que o sistema recomendou algo que ele não recomendou na hora em
que importava.

---

## Consequências

**Ganhos**

- A regra de velocidade é correta sob concorrência, com prova exata.
- Retry deliberado, observável por log estruturado e por contador de métrica —
  sem política global de retry (`CLAUDE.md` 37).
- Outbox pronta: a Fase 5 publica sem migração de esquema.
- Contenção vira 503 acionável, não 500 opaco.

**Custos aceitos**

- Um comando a mais por operação (`SET LOCAL`), medido em 8 comandos SQL no
  total.
- Latência adicional sob contenção, limitada pelo orçamento de tentativas.
- Tabela pequena ainda produz conflitos falsos; o comportamento é correto, o
  custo é retentativa.

**Dívida registrada**

- O despachante da Outbox não existe: as linhas ficam pendentes até a Fase 5.
- Não há métrica exportada — só o `Meter` declarado. Coleta é Fase 12.
- Baseline medida em uma máquina só, sem carga concorrente sustentada.

---

## Defeitos encontrados e corrigidos nesta fase

**1. Conflitos falsos entre clientes sem relação.** Descrito na Decisão 5.
Encontrado por medição, não por revisão de código.

**2. SQL cru ignora o filtro global de tenant.** Um teste desta fase lia
`eventos_de_saida` com `SqlQuery` e passava sozinho, mas falhava junto dos
outros — porque enxergava os eventos dos demais tenants. É a mesma armadilha que
o `CLAUDE.md` seção 86 quer prevenir, aparecendo no lugar menos vigiado. A
consulta passou a filtrar a organização explicitamente, com o motivo escrito ao
lado.
