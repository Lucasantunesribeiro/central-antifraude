# Security Gate 4 — Avaliação síncrona e concorrência

Data da execução: **2026-09-04**
Escopo: o que muda quando as requisições chegam juntas.

Testes em `tests/CentralAntifraude.IntegrationTests/SecurityGate4Tests.cs`,
`ConcorrenciaTests.cs` e `AvaliacaoCongeladaTests.cs`.

---

## A pergunta desta fase

As proteções das fases anteriores foram verificadas **uma requisição por vez**.
Concorrência é onde elas costumam falhar de verdade: a validação que passa duas
vezes, o isolamento que vaza porque duas transações leram ao mesmo tempo, o
cliente que desiste no meio e repete.

Cada teste aqui existe porque a falha correspondente seria **invisível** em um
cenário sequencial.

---

## Itens exigidos pelo `ROADMAP.md` (seção 4.9)

| # | Item | Resultado | Teste |
|---|---|---|---|
| 1 | Race para duplicar transação | ✅ 1× 201, 19× 200, 1 linha | `Vinte_requisicoes_identicas_produzem_uma_transacao_e_uma_avaliacao` |
| 2 | Race para duplicar avaliação | ✅ 1 avaliação, 1 evento | idem |
| 3 | Serialização / retry | ✅ retry dispara e é registrado | `O_retry_de_concorrencia_realmente_dispara_e_e_registrado` |
| 4 | Replay depois de nova rule version | ✅ devolve a avaliação original | `Replay_depois_de_publicar_regra_nova_devolve_a_avaliacao_original` |
| 5 | Timeout do cliente seguido de retry | ✅ nada duplicado, nada órfão | `Cliente_que_desiste_no_meio_e_repete_nao_duplica_nada` |
| 6 | Entrada maliciosa em concorrência | ✅ 6 recusadas, 6 aceitas, nenhuma contaminação | `Payload_malicioso_em_meio_a_rajada_e_recusado_sem_contaminar_o_resto` |
| 7 | Tenant isolation em consultas históricas | ✅ 3 ângulos | ver abaixo |

---

## 3. O retry é real, e o log prova

Sem essa verificação, os testes de concorrência poderiam estar passando porque a
máquina serializou as requisições por acaso — e o mecanismo de retry nunca teria
sido exercitado.

O log estruturado é a evidência. Ele registra **tentativa e motivo**, e o teste
verifica que ele **não** carrega o identificador do cliente:

```
Operacao critica refeita apos conflito de concorrencia. Tentativa 2, motivo sqlstate:40001.
```

Nenhum payload, credencial, chave de idempotência ou identificador de cliente
aparece ali (`CLAUDE.md` seção 70).

---

## 4. Replay não recalcula, nem depois de publicar regra nova

O cenário que o ROADMAP seção 4.6 chama de invariante crítica:

1. o integrador envia uma transação e recebe uma decisão;
2. o perfil ganha a versão 2, com pesos dobrados e limiares mais baixos;
3. o integrador repete a **mesma** requisição.

Verificado que a resposta do replay traz o mesmo `score`, a mesma `decisao` e o
mesmo `avaliadaEm` — e que a avaliação continua apontando para a **versão 1** do
perfil.

O outro lado da mesma invariante também é testado: uma transação que chega
**depois** da publicação usa a versão 2
(`Transacao_nova_depois_da_regra_nova_usa_a_versao_nova`). Congelar o passado
não pode congelar o futuro.

---

## 5. Timeout do cliente

O caso mais comum de retry no mundo real, e o mais perigoso: o integrador
estourou o próprio timeout e não sabe se a transação foi registrada.

Dois testes:

| Teste | O que garante |
|---|---|
| `Cliente_que_desiste_no_meio_e_repete_nao_duplica_nada` | Seis cancelamentos em prazos crescentes, depois um retry normal → **1 transação, 1 avaliação, 1 evento** |
| `Requisicao_cancelada_nao_deixa_transacao_sem_avaliacao` | Doze cancelamentos → toda transação gravada tem avaliação |

O segundo é a invariante que o boundary transacional existe para garantir. Uma
transação sem avaliação seria um estado que nenhuma tela sabe mostrar e que
nenhuma regra sabe corrigir depois.

---

## 6. Entrada maliciosa sob concorrência

Doze requisições simultâneas: seis legítimas, seis carregando `score` no corpo.

O risco específico da concorrência é uma validação que dependa de estado
compartilhado "vazar" entre requisições sob carga — a maliciosa passaria porque
a validação da vizinha rodou primeiro.

Resultado: **6 criadas, 6 recusadas com 400**, nenhuma linha das maliciosas no
banco, e as legítimas intactas.

O detector de PAN da Fase 2 também é reexercitado sob rajada
(`Rajada_com_pan_valido_e_recusada_inteira`): oito requisições simultâneas com
um número que passa no Luhn, todas recusadas, zero linhas.

---

## 7. Isolamento de tenant sob concorrência

Três ângulos, todos com os dois tenants ativos ao mesmo tempo:

| Teste | O que prova |
|---|---|
| `Dois_tenants_avaliando_o_mesmo_cliente_ao_mesmo_tempo_nao_se_misturam` | Mesmo `clienteExternoId` nos dois; A manda 6 (dispara velocidade 3×), B manda 2 (**zero** sinais) |
| `Cada_tenant_so_enxerga_os_proprios_eventos_de_saida` | A Outbox guarda score, decisão e sinais — e é filtrada por tenant |
| `Consulta_historica_de_um_perfil_de_leitura_continua_isolada_sob_carga` | Um Auditor do tenant B lendo enquanto A ingere 10 transações não vê nenhuma delas |

O primeiro é o vazamento mais silencioso que existiria: nenhum dado de A
apareceria na tela de B, mas o histórico de A estaria **mudando as decisões**
de B.

O segundo importa porque a Fase 5 vai ler `eventos_de_saida` **em lote, fora de
uma requisição** — que é exatamente quando um filtro esquecido passa
despercebido.

---

## Achados desta execução

**1. SQL cru ignora o filtro global de tenant.** Um teste desta fase lia
`eventos_de_saida` com `SqlQuery` e passava sozinho, mas falhava junto dos
outros — porque enxergava os eventos dos demais tenants. O filtro do EF Core não
alcança SQL bruto.

Não é uma falha de produção: hoje nenhum código de `src/` usa SQL cru para ler
dados de tenant. Mas é o alerta certo na hora certa, porque a Fase 5 vai ter
vontade de usar `FOR UPDATE SKIP LOCKED` — e ali o filtro também não vai
valer. Registrado no ADR 0009 e neste documento para que a Fase 5 já comece com
isso em mente.

**2. Contenção esgotada virava 500.** Corrigido: agora é **503 com
`Retry-After`** e código `contencao_de_concorrencia`. 500 diria "algo quebrou,
não insista"; 503 diz "estava disputado, repita" — e repetir com a mesma chave
de idempotência é seguro por construção.

---

## O que este gate **não** cobre

- **Rate limit distribuído.** O limite continua por instância do processo.
  Tratamento distribuído é a Fase 11.
- **Publicação de eventos.** A Outbox só grava. Despacho, SQS, Inbox e DLQ são a
  Fase 5, com o Security Gate correspondente.
- **Carga sustentada.** Todos os cenários são rajadas curtas. Ver
  `docs/baseline-de-performance.md` para o que foi e o que não foi medido.
- **Verificação visual no navegador.** A extensão do Chrome segue desconectada
  nesta máquina desde a Fase 2. Dívida registrada.

---

## Resultado

**Security Gate 4: verde.**

7 testes em `SecurityGate4Tests`, 5 em `ConcorrenciaTests`, 7 em
`AvaliacaoCongeladaTests`, 3 em `BaselineDePerformanceTests`. Nenhum item do
ROADMAP seção 4.9 ficou sem verificação executável.
