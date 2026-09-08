# ADR 0019 — Deploy serverless, e o despachante que é acordado em vez de perguntar

**Data:** 2026-09-08
**Status:** aceito
**Fase:** 14 — Infraestrutura, Custo e Deploy

---

## Contexto

O backbone assíncrono das fases 5 a 12 funciona assim: a transação é avaliada e
o evento é gravado na Outbox dentro da mesma transação; um despachante em
processo consulta a tabela **a cada 2 segundos** e publica o que encontrar.

Em processo, essa consulta é grátis. Na nuvem escolhida, não é.

O plano gratuito do Neon dá **100 CU-horas por mês** e suspende o compute após
**5 minutos de inatividade**. Um mês tem 730 horas. Mesmo na menor unidade de
autoscaling:

```
730 h × 0,25 CU = 182 CU-horas   →   quase o dobro do gratuito
```

Uma consulta a cada 2 segundos impede a suspensão para sempre. **O único jeito
de esta arquitetura custar dinheiro é manter o banco acordado — e o desenho
atual faz exatamente isso.**

A tabela abaixo é o que decidiu a fase:

| Varredura | Banco acordado | CU-horas/mês | Cabe no gratuito? |
|---|---|---|---|
| a cada 2 s (em processo) | 100% | ~182 | não |
| a cada 5 min | ~100% — nunca chega a suspender | ~182 | não |
| a cada 15 min | ~33% | ~61 | sim |
| a cada 1 h | ~8% | ~15 | sim, com folga |

O problema de simplesmente afastar a varredura é a latência: a 15 minutos, um
alerta poderia demorar 15 minutos para existir. Numa demonstração, isso lê como
sistema quebrado.

---

## Decisão

**Publicação imediata, varredura esparsa.** Duas coisas acionam o despachante,
e elas têm papéis diferentes:

1. **A API o invoca de forma assíncrona logo depois do commit.** É o caminho
   normal, e é o que faz um alerta existir em segundos. O banco já estava
   acordado — a requisição acabou de usá-lo.
2. **Um agendamento a cada 15 minutos varre a Outbox.** É a rede de segurança:
   o aviso do item 1 acontece depois do commit, e pode falhar.

Sem o item 2, uma invocação perdida deixaria um evento parado para sempre. Sem
o item 1, todo alerta esperaria até quinze minutos. **A garantia vem do
segundo; a experiência, do primeiro.**

O resto da infraestrutura segue o que o `CLAUDE.md` seção 74 já previa: Lambda
com Function URL para a API, Lambda para os três workers, SQS Standard com DLQ,
EventBridge Scheduler, CloudWatch e SSM Parameter Store. Nenhum recurso com
custo fixo — sem NAT Gateway, sem RDS, sem API Gateway.

---

## O que isso NÃO muda

**A Outbox continua sendo a garantia.** A invocação imediata é otimização de
latência, e nada mais. Ela acontece *depois* do commit e pode falhar; quando
falha, a falha é engolida de propósito, porque derrubar a resposta ao
integrador transformaria um atraso de até quinze minutos num erro — e ele
reenviaria a mesma transação, para receber o mesmo erro.

A posição da chamada é a decisão inteira, e é frágil o bastante para ter teste
próprio (`DespachoImediatoTests`). Se o aviso saísse de *dentro* da transação,
o despachante leria a Outbox antes do commit: numa vez não acharia nada, e
noutra publicaria um evento cuja transação ainda pode ser desfeita por falha de
serialização. **Nenhum dos dois defeitos aparece como erro** — aparecem como
alerta que não chega, ou como alerta de uma transação que nunca existiu. Por
isso o teste não confere que o aviso aconteceu: confere de onde ele enxerga o
mundo, abrindo uma conexão própria com o PostgreSQL.

**O consumidor de eventos não foi reescrito.** O SQS com gatilho de Lambda
inverte os dois extremos do contrato de fila — a AWS entrega as mensagens no
evento e apaga sozinha o que a invocação não reportar como falha. Havia duas
saídas: reescrever o consumidor para o modelo do Lambda, ou adaptar o modelo do
Lambda ao contrato que já existe.

Escolhemos a segunda. `ProcessadorDeEventos` carrega tudo o que dez fases
construíram — Inbox, savepoint por efeito, conferência de tenant, redrive — e
reescrevê-lo significaria reprovar essas garantias em código novo. A ponte é
`FilaDoEventoLambda`, que finge ser uma fila.

---

## A parte que quase deu errado

O adaptador conta as falhas **por subtração**, e não somando as devoluções.

O consumidor confirma o sucesso chamando `ApagarAsync` e devolve o que recusa
chamando `DevolverAsync` — mas ele **não devolve o que falha por erro
transitório**. Ali ele deixa a visibilidade expirar, que é o comportamento
certo quando a fila é uma tabela.

Somar as devoluções deixaria essas mensagens sem reporte, e a AWS as apagaria
**como se tivessem dado certo**. Perda silenciosa, que é o pior desfecho
possível numa fila. Entregue e não confirmado significa "não terminou bem",
qualquer que tenha sido o motivo.

---

## Consequências

**Boas**

- Custo projetado de US$ 0,00, com o item mais apertado em 16% da cota.
- Alerta em segundos no caminho normal, sem polling.
- O código de dentro do ciclo não mudou; mudou quem aciona o ciclo.
- Falha parcial por item no SQS: uma mensagem ruim no meio de nove boas devolve
  uma só.

**Ruins, e assumidas**

- O primeiro acesso depois de horas de silêncio espera o Neon acordar somado ao
  arranque frio do .NET. Numa demonstração de portfólio esse é o caso **comum**,
  e o número não existe até medir na nuvem.
- Se a invocação imediata falhar, o evento espera até quinze minutos. É o preço
  explícito de não manter o banco acordado.
- As permissões IAM que o SAM gera a partir das políticas não foram exercidas:
  o teste de permissão é o deploy.

---

## Alternativas descartadas

**Manter o laço em processo dentro da API.** Um `BackgroundService` numa Lambda
roda apenas enquanto a instância está quente — ou seja, quase nunca, e de forma
imprevisível. Seria um laço que parece existir e não existe.

**Varredura a cada 5 minutos, sem invocação imediata.** Não resolve o custo: 5
minutos é exatamente o limiar de suspensão do Neon, então o banco nunca chega a
suspender. Mesmas 182 CU-horas, com latência pior.

**FIFO em vez de Standard.** Não é sobre custo: `CLAUDE.md` seção 31 já decidiu
que o sistema precisa funcionar com entrega duplicada, e usar FIFO seria
mascarar a idempotência em vez de prová-la.

**Segredo em variável de ambiente do Lambda.** É a opção mais fácil e a mais
traiçoeira: parece segura e aparece em texto puro para quem abrir a configuração
da função no console. Os segredos ficam no Parameter Store como `SecureString`,
criados fora do template.

---

## Referências

- `CLAUDE.md` seções 31, 38, 39, 40, 59, 74, 75, 76, 109
- [ADR 0010 — Backbone assíncrono](0010-backbone-assincrono.md)
- [docs/custo-e-infraestrutura.md](../custo-e-infraestrutura.md)
- [Lambda .NET runtimes](https://docs.aws.amazon.com/lambda/latest/dg/lambda-csharp.html) — `dotnet10`, Amazon Linux 2023, depreciação em 14/11/2028
