# Security Gate 5 — Backbone assíncrono

Data da execução: **2026-09-04**
Escopo: o que o consumidor faz com uma mensagem que não devia estar ali.

Testes em `tests/CentralAntifraude.IntegrationTests/SecurityGate5Tests.cs`,
`BackboneAssincronoTests.cs` e `FilaDeMensagensTests.cs`.

---

## A premissa desta fase

Até agora, tudo que entrava no sistema passava por autenticação. A fila muda
isso: o worker lê de um lugar, aplica efeito e **não tem um usuário do outro
lado para recusar**.

O que chega da fila é **dado**, nunca instrução. Cada item abaixo é uma forma
diferente de a mensagem tentar ser tratada como instrução.

---

## Itens exigidos pelo `ROADMAP.md` (seção 5.10)

| # | Item | Resultado | Teste |
|---|---|---|---|
| 1 | Mensagem forjada | ✅ recusada, sem efeito | `Envelope_que_declara_um_tenant_e_aponta_para_outro_e_recusado` |
| 2 | Event type desconhecido | ✅ recusado | `Mensagem_que_o_worker_nao_entende_nao_vira_efeito` |
| 3 | Versão desconhecida | ✅ recusada sem interpretar o payload | `Versao_desconhecida_e_recusada_mesmo_com_o_tipo_certo` |
| 4 | Tenant inconsistente | ✅ conferido contra o banco | `Envelope_que_declara_um_tenant_e_aponta_para_outro_e_recusado` |
| 5 | Payload inválido | ✅ 12 formas testadas | `Mensagem_que_o_worker_nao_entende_nao_vira_efeito` |
| 6 | Replay | ✅ 5 replays, efeito 1× | `Replay_de_uma_mensagem_antiga_nao_soma_de_novo` |
| 7 | Secret/configuração fora de log | ✅ nada do tenant no log | `O_log_do_backbone_nao_carrega_conteudo_de_evento_nem_segredo` |
| 8 | Poison message | ✅ sai de circulação | `Mensagem_envenenada_sai_de_circulacao_e_fica_guardada` |
| 9 | DLQ behavior | ✅ guarda corpo, contagem e motivo | `Mensagem_morta_guarda_o_corpo_e_o_motivo` |

---

## 2, 3 e 5. Doze formas de a mensagem estar errada

Uma tabela de teoria cobre, com a **mesma resposta certa** para todas — nenhum
efeito, mensagem preservada para a DLQ:

| Caso | Caso |
|---|---|
| não é JSON | JSON que não é objeto |
| sem `eventId` | sem `tenantId` |
| sem `payload` | sem `occurredAt` |
| tipo desconhecido | versão desconhecida (99) |
| versão zero | `eventId` vazio |
| `eventId` não é GUID | `payload` não é objeto |

Nenhuma delas pode virar "efeito com valores padrão". **Inventar o que faltava
seria pior do que recusar**: produziria um número plausível no painel de alguém.

### Por que a versão é um campo separado

Uma `v2` chegando em um processo que só entende a `v1` é recusada **sem tentar
interpretar o payload**. Os campos podem ter mudado de significado — ler
`score` de uma v2 com o código da v1 poderia produzir um número plausível e
errado.

---

## 1 e 4. Mensagem forjada e tenant inconsistente

Este é o ataque mais silencioso desta camada: a mensagem é válida, o payload é
um evento real, mas o `tenantId` foi trocado. Se o worker confiasse no campo, o
número do cliente A apareceria no painel do cliente B — **sem erro nenhum no
caminho**.

O `tenantId` é uma **afirmação de quem enviou**. O worker carrega a transação
citada e confere de quem ela é. Verificado:

| Cenário | Resultado |
|---|---|
| Envelope válido com `tenantId` do outro tenant | recusado; **zero** somado nos dois |
| Envelope válido apontando para transação inexistente | recusado; zero somado |
| Eventos legítimos de dois tenants no mesmo lote | cada um soma no painel certo |

---

## 6. Replay

Um replay é indistinguível de uma reentrega normal — e por isso a resposta é a
mesma. Cinco reenvios da mesma mensagem legítima: **5 repetidos, 0 processados,
contador em 1, uma linha na Inbox**.

O contador é quem denuncia. Se a Inbox falhasse, ele estaria em 6 e nada mais
mudaria — nenhuma exceção, nenhum log, nenhum registro fora do lugar.

---

## 7. O log não repete o conteúdo da mensagem

O log do caminho assíncrono diz **o que aconteceu** sem repetir **o que estava
dentro**. Score, decisão e identificador de cliente são dado do tenant
(`CLAUDE.md` 70).

Verificado que o log de uma ingestão completa, despachada e consumida **não
contém**: o `clienteExternoId`, a referência do instrumento nem a chave da API.

O que entra: identificador do evento, tipo, contagem de entregas, motivo da
recusa e o **identificador de correlação** — que é um número de protocolo, e não
dado do cliente.

---

## 8 e 9. Veneno e DLQ

| Comportamento | Verificado |
|---|---|
| Mensagem inválida **não é apagada** | o worker devolve; o redrive decide |
| Sai de circulação depois do máximo de entregas | `MaximoDeRecebimentos` = 5 |
| A DLQ guarda o corpo, a contagem e o motivo | `Mensagem_morta_guarda_o_corpo_e_o_motivo` |
| Uma envenenada **não bloqueia** as boas | 4 boas processadas, 1 morta |
| Uma ruim no meio do lote não força reprocessar as boas | 3 processadas, 1 recusada |

Apagar em silêncio esconderia o problema. Se a mensagem envenenada segurasse a
fila, um único registro defeituoso pararia a operação inteira — e o painel
simplesmente deixaria de atualizar, sem ninguém entender por quê.

---

## Itens adicionais verificados

| Item | Resultado | Onde |
|---|---|---|
| A DLQ não é alcançável por rota HTTP | ✅ 5 caminhos testados, todos 404 | `A_fila_de_mortas_nao_e_alcancavel_por_nenhuma_rota_http` |
| A Outbox é filtrada por tenant na leitura de aplicação | ✅ | filtro global no `DbContext` |
| O evento não carrega instrumento, dispositivo nem IP | ✅ | herdado da Fase 4, reverificado |
| Recibo antigo não apaga a entrega seguinte | ✅ | `Recibo_antigo_nao_apaga_a_entrega_seguinte` |
| Dois consumidores nunca recebem a mesma mensagem | ✅ 20 mensagens, 20 recibos distintos | `Dois_consumidores_nunca_recebem_a_mesma_mensagem` |

---

## Achado desta execução

**A fila guardava o corpo como `jsonb`, e isso a tornava mais segura do que
deveria — no sentido ruim.** Um corpo corrompido era recusado **pelo banco**, e
não pelo consumidor. O caminho de mensagem envenenada nunca era exercitado: o
`INSERT` falhava antes.

Isso é o oposto de uma proteção. Em produção, com SQS, o corpo é uma cadeia de
bytes opaca e a mensagem corrompida **chega** — e o consumidor precisa saber o
que fazer com ela. Corrigido para `text`, e o caminho de veneno passou a ser
testável de verdade.

---

## O que este gate **não** cobre

- **Autenticação da própria fila.** Hoje ela é uma tabela no mesmo banco; o
  controle de acesso é o do banco. Com SQS, entra política IAM — Fase 14.
- **Criptografia em repouso da mensagem.** O corpo carrega score e decisão. Em
  produção isso vira `SSE` na fila e criptografia do Neon; não há decisão a
  tomar aqui ainda.
- **Reprocessamento da DLQ.** Existe a estratégia (inspecionar, corrigir a
  causa, reenviar) mas não a ferramenta. Fase 12.
- **Rate limit no consumo.** O worker processa o que a fila entregar. Não há
  cenário hoje em que isso seja um risco.
- **Verificação visual no navegador.** Extensão do Chrome desconectada desde a
  Fase 2. Nenhuma tela nova nesta fase.

---

## Resultado

**Security Gate 5: verde.**

21 testes em `SecurityGate5Tests`, 11 em `BackboneAssincronoTests`, 10 em
`FilaDeMensagensTests` e 10 unitários de envelope. Nenhum item do ROADMAP seção
5.10 ficou sem verificação executável.
