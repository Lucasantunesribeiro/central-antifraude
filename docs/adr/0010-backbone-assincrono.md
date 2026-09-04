# ADR 0010 — Backbone assíncrono: Outbox, fila, Inbox e DLQ

- **Status:** aceito
- **Data:** 2026-09-04
- **Fase:** 5 — Backbone Assíncrono

## Contexto

A Fase 4 deixou a Outbox gravando eventos que ninguém lia. Esta fase fecha o
caminho: publicar, consumir e aplicar um efeito — **uma vez**, mesmo com
entrega repetida, processo caindo no meio e vários despachantes ao mesmo tempo.

O `CLAUDE.md` fixa a premissa (seção 31):

> **at-least-once + consumidores idempotentes.**

E a seção 40 é explícita sobre o que **não** se promete:

> Se a mensagem for enviada ao broker e o processo cair antes de marcar o
> outbox como publicado, a mensagem pode ser enviada novamente. Isso é
> comportamento esperado.

Não há promessa de entrega única. Há promessa de **efeito único**.

---

## Decisão 1 — A fila é PostgreSQL, e não SQS, até a Fase 14

O ROADMAP seção 5.5 é explícito: *"Infra cloud real pode continuar
simulada/local até Fase 14"*. Criar fila remota agora seria recurso de nuvem
sem autorização (`CLAUDE.md` 107.5 e 109).

**O que foi descartado, e por quê:**

| Alternativa | Por que não |
|---|---|
| SQS real agora | Recurso remoto sem autorização; e o SDK ficaria nove fases sem ser exercitado de verdade |
| Fila em memória | Entregaria **uma vez**, **em ordem** e **sem visibilidade** — as três garantias que o SQS Standard não dá. Os testes passariam e o defeito apareceria em produção |
| LocalStack | Um container a mais no CI para exercitar um adaptador que ainda não existe |

A escolha foi **reproduzir o contrato**, não simplificá-lo:

| Comportamento | Aqui | No SQS |
|---|---|---|
| Entrega ao menos uma vez | receber esconde, apagar remove | idem |
| Sem ordem garantida | `SKIP LOCKED` entrega o que estiver livre | idem |
| Visibilidade | `disponivel_em` no futuro | `VisibilityTimeout` |
| Recibo por entrega | `recibo` sorteado a cada recebimento | `ReceiptHandle` |
| Contagem de entregas | `recebimentos` | `ApproximateReceiveCount` |
| Redrive para DLQ | move ao passar do máximo | `maxReceiveCount` |

Os limites da configuração são os do próprio SQS — visibilidade até 12 h, lote
até 10 — para que nenhuma configuração criada aqui deixe de valer na Fase 14.

**O que torna a troca uma troca, e não uma reescrita:** `FilaDeMensagensTests`
descreve o contrato sem citar PostgreSQL em lugar nenhum. O adaptador do SDK
passa a responder aos mesmos dez testes.

---

## Decisão 2 — O despachante publica antes de marcar

```text
BEGIN
  trava as pendentes com FOR UPDATE SKIP LOCKED
  publica na fila
  marca como publicado
COMMIT
```

A ordem é a decisão inteira. Se o processo cair entre publicar e marcar, a
mensagem sai de novo no próximo ciclo — **duplicata**, que o consumidor
idempotente resolve. Marcar antes trocaria isso por **evento perdido**, que
ninguém resolve.

`SKIP LOCKED` permite vários despachantes sem coordenação externa: quem chega
depois pula as linhas já travadas em vez de esperar. Verificado com três
despachantes simultâneos sobre doze eventos — cada um publicado exatamente uma
vez.

Falha ao publicar um evento **não derruba o lote**: aquele evento continua
pendente, com a tentativa contada, e volta no próximo ciclo.

---

## Decisão 3 — O envelope separa tipo e versão

```json
{
  "eventId": "...", "eventType": "TransacaoAvaliada", "version": 1,
  "tenantId": "...", "correlationId": "...", "occurredAt": "...",
  "payload": { }
}
```

Metadados **fora** do payload, de propósito. Um consumidor precisa decidir se
sabe ler a mensagem **antes** de interpretá-la. Se tipo e versão estivessem
enterrados no payload, ele teria que desserializar para descobrir que não
deveria ter desserializado.

Tipo e versão em campos **separados** permitem a pergunta "eu sei lidar com
isto em alguma versão?" sem parsing de string. Um processo que entende a v1 e
recebe a v2 recusa de forma explícita — ler `score` de uma v2 com o código da
v1 poderia produzir um número plausível e errado, que é pior do que a recusa.

Este JSON é o contrato público: quando a Fase 14 trocar a fila, é ele que vai
no corpo da mensagem, sem mudança.

---

## Decisão 4 — A Inbox decide pela restrição única, não por consulta

```text
BEGIN
  grava a Inbox        ← a restrição única é quem decide
  aplica o efeito
COMMIT
apaga da fila
```

**Inbox e efeito no mesmo commit.** Separados, existiria o instante em que o
efeito aconteceu e a marca não — e a reentrega repetiria o efeito, que é
exatamente o que a Inbox deveria impedir.

**Sem consulta prévia.** `SELECT` seguido de `INSERT` é a mesma armadilha que a
ingestão já evita: dois workers concorrentes passariam os dois pela consulta e
somariam duas vezes.

**A chave inclui o consumidor.** Dois consumidores precisam tratar o mesmo
evento, cada um uma vez. Marcar só pelo evento faria o segundo achar que o
trabalho dele já foi feito.

**A mensagem só é apagada depois do commit.** Se o processo cair no meio, ela
volta, a Inbox reconhece e o efeito não acontece de novo. O contrário trocaria
duplicata por perda.

---

## Decisão 5 — O efeito é um contador, e a escolha é deliberada

O ROADMAP 5.6 pede um efeito *"simples e verificável"*. `resumo_diario_de_decisoes`
conta transações por decisão, por dia, por organização.

**Um contador é o pior caso para entrega duplicada** — e é exatamente por isso
que ele foi escolhido. Somar duas vezes não deixa rastro nenhum: o número
simplesmente fica errado.

Um efeito com chave única passaria **mesmo com a Inbox desligada**, porque a
restrição do banco seguraria a duplicata. O contador não perdoa: ou a Inbox
funciona, ou o total mente.

O incremento é `INSERT ... ON CONFLICT DO UPDATE`. Ler, somar e gravar em
passos separados perderia incrementos sob concorrência.

O dia é o da **avaliação**, em UTC — não o da ocorrência. Uma transação
atrasada de três dias atrás foi decidida hoje, e é hoje que ela entrou na fila
do analista. Agrupar pela ocorrência faria o painel de hoje mudar
retroativamente sempre que um evento atrasado chegasse.

---

## Decisão 6 — O que chega da fila é dado, nunca instrução

O worker não tem um usuário do outro lado para recusar. Três verificações,
todas antes de qualquer efeito:

| Verificação | O que ela impede |
|---|---|
| O envelope é interpretável? | JSON corrompido, campo faltando, versão desconhecida |
| O conteúdo bate com o que o envelope declara? | Mensagem forjada, defeito de despacho |
| O tenant declarado confere com o dado? | O número do cliente A aparecer no painel do cliente B |

A terceira é a mais importante e a mais silenciosa. O `tenantId` é uma
**afirmação de quem enviou**. O worker carrega a transação citada — ignorando o
filtro global, porque aqui a pergunta é justamente "de quem é isto?" — e
confere. Um envelope que declare o tenant A e aponte para uma transação do
tenant B é recusado.

---

## Decisão 7 — Não se apaga o que não se entende

Uma mensagem inválida **não é apagada**. Ela é devolvida, a contagem de
entregas cresce, e a política de redrive a move para a DLQ depois do máximo
configurado.

Apagar em silêncio esconderia o problema. A DLQ existe para guardar a mensagem
para análise humana (`CLAUDE.md` 72) — com o corpo, a contagem de entregas e o
motivo.

Devolver na hora, em vez de esperar a visibilidade expirar, só **acelera** o
caminho até lá — é o equivalente de um `ChangeMessageVisibility(0)`.

E uma mensagem envenenada **não bloqueia as boas**: verificado com uma mensagem
corrompida no meio de três válidas — as três são processadas, só a ruim
continua circulando até a DLQ.

---

## Decisão 8 — Os laços de fundo vêm desligados por padrão

`SegundoPlano:Habilitado` é `false` por padrão, ligado em `Development`.

Nos testes de integração, um laço rodando sozinho tornaria "quantas mensagens
sobraram na fila" uma pergunta sem resposta estável, e o resultado passaria a
depender de quem ganhou a corrida. Cada ciclo é chamado explicitamente, e o que
o teste afirma é o que de fato aconteceu.

Cada ciclo abre o próprio escopo de injeção. Sem isso, o `DbContext` viveria
pelo tempo do processo inteiro, acumulando entidades rastreadas até consumir a
memória. Uma falha em um ciclo nunca derruba o laço.

Na Fase 14 estes laços viram Lambdas; **o código de dentro do ciclo não muda**.

---

## Decisão 9 — Duas filas desde já

`eventos-operacionais` e `backtests` existem separadas desde agora
(`CLAUDE.md` 30). Backtest é trabalho longo e em lote; ele não pode ficar na
frente de um alerta operacional na mesma fila. A Fase 9 usa a segunda.

---

## Consequências

**Ganhos**

- Efeito único provado contra as cinco falhas obrigatórias do ROADMAP 5.11.
- Correlação atravessa HTTP → domínio → Outbox → mensagem → worker → **efeito
  gravado**, e não apenas logado.
- Contrato de fila testado de forma independente da implementação: a Fase 14 é
  um adaptador, não uma reescrita.
- DLQ com corpo, contagem e motivo — investigável.

**Custos aceitos**

- A fila divide o banco com os dados. Em volume de portfólio isso não é
  problema; em volume real seria, e é a Fase 14 que resolve.
- O despachante lê a Outbox de todos os tenants. É inerente: ele roda sem
  identidade. Nada do que ele lê sai do processo.
- Um ciclo de despacho e um de consumo por rodada: o efeito aparece com atraso
  de até um intervalo de laço.

**Dívida registrada**

- Sem métrica exportada de profundidade de fila e idade da mensagem mais antiga
  — Fase 12.
- Sem procedimento automatizado de reprocessamento da DLQ. A inspeção é por
  acesso administrativo ao banco.
- A projeção diária não é exposta por nenhuma rota. A tela é a Fase 10.

---

## Defeitos encontrados nesta fase

**1. SQL cru NÃO escapa do filtro global de tenant.** O despachante usava
`FromSql` para o `FOR UPDATE SKIP LOCKED` e não publicava nada — zero eventos,
sem erro nenhum no caminho. O EF Core compõe o filtro **por cima** do `FromSql`,
como se fosse uma subconsulta; como o despachante roda sem identidade, o tenant
efetivo é vazio e a consulta devolvia zero linha.

A Fase 4 já tinha registrado a suspeita ao encontrar o inverso em um teste. Aqui
ela apareceu em produção de verdade, no caminho principal. Corrigido com
`IgnoreQueryFilters` **pelo nome do filtro** — qualquer outro filtro que venha a
existir continua valendo.

**2. A fila guardava o corpo como `jsonb`, e isso a deixava mais rígida do que o
SQS.** Duas consequências: um corpo corrompido era recusado **pelo banco** em vez
de virar mensagem envenenada para o consumidor tratar, e um corpo válido voltava
com as chaves reordenadas, escondendo qualquer teste sobre o formato de fio
exato. Para o SQS, o corpo de uma mensagem é uma cadeia de bytes opaca.
Corrigido para `text`.
