# Observabilidade — o que o sistema conta sobre si mesmo

Catálogo de logs e métricas, e o procedimento de fila de mortas.

Este documento existe para responder a três perguntas que ninguém consegue
responder lendo o código: **quais métricas existem**, **o que uma linha de log
carrega** e **o que fazer quando uma mensagem falha para sempre**.

---

## 1. O fio: seguir uma operação de ponta a ponta

Toda requisição recebe um `CorrelationId`. Ele acompanha:

```text
HTTP  →  avaliação  →  Outbox  →  publicação  →  fila  →  worker  →  efeito
```

Os quatro elos que aparecem **no log**, todos com o mesmo `CorrelationId`:

| EventId | Onde | O que diz |
|---|---|---|
| `100` | `MiddlewareDeTelemetria` | operação, status e duração da requisição |
| `502` | `DespachanteDeEventos` | evento publicado, com tipo e fila de destino |
| `514` | `ProcessadorDeEventos` | efeito aplicado, com o desfecho |
| `610` | `CriadorDeAlertas` | alerta criado, com prioridade |

Para investigar uma transação específica, o caminho é:

```bash
# 1. o integrador tem o identificador: ele volta no cabeçalho da resposta.
curl -i -X POST .../api/ingestao/transacoes ... | grep X-Correlation-Id

# 2. no log estruturado, filtre pela propriedade — e não pelo texto.
#    Em CloudWatch Logs Insights:
#      fields @timestamp, EventId, @message
#      | filter CorrelationId = "abc123..."
#      | sort @timestamp asc
```

O `CorrelationId` também está gravado no banco, em `transacoes`,
`eventos_de_saida` e `eventos_processados` — o log some com a retenção, e a
pergunta continua valendo depois disso.

**Valor recebido do cliente é validado.** Só `[A-Za-z0-9_-]`, entre 8 e 64
caracteres; qualquer outra coisa é substituída por um identificador gerado. Sem
isso, uma quebra de linha no cabeçalho permitiria forjar uma linha de log
inteira.

---

## 2. O que **não** entra no log

| Nunca aparece | Por quê |
|---|---|
| Chave de integração, access token, refresh token | credencial (`CLAUDE.md` 50 e 56) |
| Corpo do evento | carrega score, decisão e identificador de cliente |
| Query string | a busca do console é texto digitado por um analista |
| Caminho concreto (`/api/casos/9f3c…`) | identificador de recurso de um tenant |
| Payload da requisição | idem, e por conveniência não se abre exceção |

A regra por trás das cinco linhas é a mesma: **log e métrica escapam do
perímetro que o produto inteiro defende.** O dado no banco tem filtro de tenant,
política de perfil e trilha de auditoria; a mesma informação numa linha de log
tem apenas as permissões do coletor.

Verificado por `SecurityGate12Tests` e `SecurityGate5Tests`.

---

## 3. Catálogo de métricas

Cinco medidores, todos com o prefixo `CentralAntifraude.`. O catálogo em código
é `Application/Observabilidade/Telemetria.cs`.

### `CentralAntifraude.Api`

| Instrumento | Tipo | Dimensões |
|---|---|---|
| `requisicoes_total` | contador | `operacao`, `resultado` (`2xx`/`4xx`/`5xx`) |
| `requisicao_duracao_ms` | histograma | `operacao` |

### `CentralAntifraude.Risco`

| Instrumento | Tipo | Dimensões |
|---|---|---|
| `avaliacoes_total` | contador | — |
| `avaliacao_duracao_ms` | histograma | — |
| `decisoes_total` | contador | `decisao` |

`avaliacao_duracao_ms` mede a carga do contexto **junto** com a execução das
regras. O motor é uma função pura e rápida; o que cresce com o histórico do
cliente é a consulta que o alimenta. Medir só a parte pura daria um número
bonito e inútil.

### `CentralAntifraude.Mensageria`

| Instrumento | Tipo | Dimensões |
|---|---|---|
| `outbox_publicados_total` | contador | — |
| `outbox_falhas_total` | contador | — |
| `outbox_pendentes` | estado | — |
| `outbox_idade_segundos` | estado | — |
| `mensagens_consumidas_total` | contador | `resultado` |
| `mensagens_mortas_total` | contador | `fila` |
| `fila_pendentes` | estado | `fila` |
| `fila_mortas` | estado | `fila` |
| `laco_falhas_total` | contador | `laco` |
| `backtest_duracao_ms` | histograma | — |

### `CentralAntifraude.Alertas` e `CentralAntifraude.Concorrencia`

| Instrumento | Tipo | Dimensões |
|---|---|---|
| `alertas_criados` | contador | `prioridade` |
| `operacao_critica_retentativas` | contador | — |
| `operacao_critica_tentativas_esgotadas` | contador | — |

### As dimensões são uma lista de permissão

`decisao`, `prioridade`, `resultado`, `operacao`, `fila`, `laco` — e mais
nenhuma. Cada uma tem um conjunto pequeno e fechado de valores.

Uma lista de dimensões *proibidas* envelheceria mal: ela cobre o que alguém
lembrou de proibir, e a dimensão que explode a cardinalidade é sempre a que
ninguém imaginou. Uma métrica nova com dimensão não prevista **quebra a build**
(`ObservabilidadeTests`), e quem a criou precisa declarar quantos valores ela
pode ter.

### Não há rota de métricas

Profundidade de Outbox e de fila são números do **processo**, e não de uma
organização: não há como devolvê-los ao administrador de um tenant sem contar a
ele o volume de trabalho dos outros. Os instrumentos são lidos pelos testes por
`MeterListener`, no mesmo processo, e a Fase 14 conecta um exportador aos mesmos
medidores.

---

## 4. Quatro sinais que valem alarme

Não é uma lista de SLA — é o que muda de comportamento antes de virar
incidente.

| Sinal | Por que importa |
|---|---|
| `outbox_idade_segundos` crescendo | despachante parado. **É este, e não a contagem**: 3 eventos parados há 40 minutos é problema; 300 há 2 segundos, não |
| `laco_falhas_total` subindo | o laço continua rodando após falhar — de fora, um despachante que falha em todo ciclo se parece com um ocioso |
| `mensagens_mortas_total` > 0 | trabalho que o sistema aceitou e não entregou |
| `outbox_falhas_total` subindo | publicação falhando **sem nada quebrar** — o evento fica pendente e volta |

Os dois do meio têm a mesma característica traiçoeira: nada retorna erro a
ninguém.

---

## 5. Fila de mortas: identificar, inspecionar, corrigir, reprocessar

Exigido pelo `CLAUDE.md` seção 72.

Uma mensagem vai para `mensagens_mortas` depois de `MaximoDeRecebimentos`
entregas sem confirmação. Isso é registrado como **aviso** (EventId `530`) e
conta em `mensagens_mortas_total`.

### Identificar

```sql
SELECT fila, count(*), min(movida_em), max(movida_em)
FROM mensagens_mortas
GROUP BY fila;
```

### Inspecionar

```sql
SELECT id, fila, recebimentos, inserida_em, movida_em, motivo
FROM mensagens_mortas
ORDER BY movida_em DESC
LIMIT 20;
```

O corpo da mensagem está na coluna `corpo`. Ele **não** é registrado em log —
carrega score, decisão e identificador de cliente, e fica sob a mesma
autorização do resto do dado.

Os motivos possíveis, e o que cada um significa:

| Sintoma no log | Causa provável |
|---|---|
| EventId `511` — envelope inválido | mensagem de outra origem, ou contrato mudado sem versão nova |
| EventId `512` — tenant inconsistente | envelope aponta para transação de outra organização |
| EventId `513` — falha no efeito | dependência indisponível na hora, ou defeito no manipulador |

### Corrigir a causa

Nenhuma das três se resolve reprocessando a mensagem antes de arrumar o que
falhou. Corrigido o defeito, a reentrega é segura: os consumidores são
idempotentes por `(consumidor, evento)` na Inbox, e os efeitos têm restrição
própria — `alertas.avaliacao_id` é única.

### Reprocessar

```sql
-- Devolve à fila de origem uma mensagem específica, zerando as entregas.
WITH volta AS (
    DELETE FROM mensagens_mortas
    WHERE id = '00000000-0000-0000-0000-000000000000'
    RETURNING id, fila, corpo)
INSERT INTO fila_de_mensagens (id, fila, corpo, disponivel_em, recebimentos, inserida_em)
SELECT id, fila, corpo, now(), 0, now()
FROM volta;
```

**Reprocessar é seguro por causa da Inbox, e não por causa deste comando.** Um
evento cujo efeito já aconteceu volta como "já processado" e não gera efeito
novo — é a mesma proteção que faz a entrega ao-menos-uma-vez ser aceitável.

Não existe rota HTTP para isto, de propósito: mover mensagem entre filas é
operação de manutenção, não capacidade do produto.

---

## 6. Saúde

| Rota | Responde | Toca o banco |
|---|---|---|
| `/health/live` | o processo está de pé | não |
| `/health/ready` | as dependências respondem | sim |

A separação existe para um momento específico: **com o banco fora,
`/health/live` continua 200.** Um health check único faria um banco lento
derrubar e reiniciar um processo saudável — e o reinício não consertaria o
banco, só tiraria do ar a única instância que poderia responder quando ele
voltasse.

A resposta diz **que** não está pronto, e não **por quê**. A mensagem de exceção
de um driver de banco carrega host, porta, base e usuário, e o endpoint de saúde
é a superfície mais exposta que existe. O detalhe fica no log, que é interno.

Verificado por `ResilienciaTests` e `SecurityGate12Tests`.
