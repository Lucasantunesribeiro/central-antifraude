# Security Gate 9 — Backtests

Data da execução: **2026-09-07**
Escopo: a primeira fase em que uma requisição autenticada dispara **trabalho em
lote sobre o histórico inteiro** de uma organização.

Testes em `tests/CentralAntifraude.IntegrationTests/SecurityGate9Tests.cs` e
`BacktestsTests.cs`,
`tests/CentralAntifraude.UnitTests/Dominio/ExecucaoDeBacktestTests.cs`,
`ApuracaoDoBacktestTests.cs`,
`tests/CentralAntifraude.UnitTests/Infraestrutura/SerializadorDoBacktestTests.cs`
e `tests/CentralAntifraude.ArchitectureTests/MotorUnicoTests.cs`.

---

## A premissa desta fase

Até aqui, uma requisição custava uma transação. Agora um clique manda ler
milhares de linhas e executar o motor duas vezes para cada uma.

Daí os três riscos próprios do gate:

1. **custo** — sem limite de janela, de volume e de execuções simultâneas, um
   único usuário autenticado derruba a fila de backtests;
2. **uma segunda porta para o motor** — se a configuração candidata viesse do
   corpo da requisição, o backtest seria um caminho para executar regra que
   nunca passou pelo catálogo fechado (`CLAUDE.md` seção 21);
3. **efeito operacional disfarçado de simulação** — um backtest que criasse
   alerta ou avaliação contaminaria a operação com dados de ensaio, e ninguém
   conseguiria distinguir depois.

---

## Itens exigidos pelo `ROADMAP.md` (seção 9.9)

| # | Item | Resultado | Teste |
|---|---|---|---|
| 1 | Backtest de outro tenant | ✅ `404` em leitura e cancelamento; conferido no banco | `Backtest_de_outro_tenant_responde_404_e_nao_403`, `Cancelar_backtest_de_outro_tenant_responde_404_e_nao_altera_nada` |
| 2 | Intervalo abusivo | ✅ `400` no pedido, sem gravar linha nenhuma | `Intervalo_abusivo_e_recusado_antes_de_qualquer_leitura` |
| 3 | Spam de execuções | ✅ 2 aceitas, 4 recusadas com `limite_de_execucoes` | `Spam_de_execucoes_esbarra_no_limite_por_organizacao` |
| 4 | Payload malicioso | ✅ campo desconhecido e 4 campos internos, todos `400` | `Campo_desconhecido_no_corpo_e_recusado`, `Campos_internos_nao_podem_chegar_pelo_corpo` |
| 5 | Worker duplicado | ✅ duas mensagens, dois workers em paralelo, **uma** conclusão | `Dois_workers_na_mesma_execucao_produzem_uma_conclusao_so` |
| 6 | Reprocessamento | ✅ reentrega encontra `Concluida` e não reescreve | `Entrega_repetida_nao_reexecuta_o_backtest` |
| 7 | Cancelamento | ✅ antes da execução, com versão velha e depois de concluída | `Cancelar_antes_da_execucao_impede_o_worker_de_rodar`, `Cancelar_com_versao_velha_e_recusado`, `Cancelar_o_que_ja_concluiu_e_recusado` |
| 8 | Regra candidata inválida | ✅ sem rascunho, desativada e de outro tenant | `Regra_sem_rascunho_nao_pode_ser_simulada`, `Regra_desativada_nao_pode_ser_simulada`, `Regra_de_outro_tenant_como_candidata_responde_404` |
| 9 | Efeito operacional indevido | ✅ contagens conferidas antes e depois, no banco | `O_backtest_nao_escreve_nada_em_producao`, `Um_backtest_que_bloquearia_tudo_nao_cria_alerta_nenhum` |

---

## 1. Isolamento entre tenants

O identificador da execução **e o da regra candidata** vêm do cliente. Os dois
passam pelo filtro global de tenant.

Um backtest de outro tenant responde `404`, e não `403`: um `403` já
confirmaria que aquele identificador existe em algum lugar (`CLAUDE.md`
seção 52). O mesmo vale para a regra: se o Supervisor de A pudesse apontar uma
regra de B como candidata, o resultado descreveria **a estratégia antifraude do
outro cliente**.

A verificação não para na resposta HTTP: depois de um cancelamento recusado, a
execução é lida do banco e o status confirmado como `Pendente`.

---

## 2 e 3. Custo

Cinco limites, todos configuráveis e validados na inicialização:

| Limite | Padrão | Onde é recusado |
|---|---|---|
| Janela | 90 dias | `400` no pedido |
| Transações analisadas | 5.000 | `400` no pedido, `Falhou` no worker |
| Transações de contexto | 50.000 | `Falhou` no worker |
| Execuções em andamento por organização | 2 | `409` `limite_de_execucoes` |
| Tempo de execução | 300 s | `Falhou`, com retomada possível |

**A recusa acontece no `POST`, e não no worker.** Uma janela de dez anos aceita
e enfileirada já custaria a leitura inteira; o teste confere que nenhuma linha
de execução é gravada quando o intervalo é abusivo.

**O limite de execuções é por organização, e não global.** Um limite global
faria um cliente movimentado bloquear todos os outros — negação de serviço
entre tenants pela porta da frente. Há teste para isso: A esgota a cota e B
continua sendo aceito.

**Recusar, nunca truncar.** Um resultado sobre um pedaço arbitrário do período
pareceria completo e levaria à decisão errada.

---

## 4. O que não pode chegar pelo corpo

O JSON estrito da aplicação (`CLAUDE.md` seção 54) recusa campo desconhecido.
Quatro campos internos foram testados um a um — `status`, `resultado`,
`candidato` e `versao` — e todos devolvem `400`.

`candidato` é o mais importante da lista. **O perfil candidato é montado pelo
servidor**, a partir do rascunho já gravado, com a mesma composição que a
publicação usaria. Se ele viesse do corpo, o backtest executaria configuração
que nunca passou pelo catálogo fechado — e o catálogo fechado é o que impede o
produto de virar uma DSL.

Limiares impossíveis — revisão ≥ bloqueio, revisão zero, acima de 100 — são
recusados pelas **mesmas invariantes** de uma versão de perfil de verdade.
Simular o que não poderia ser publicado descreveria um estado inalcançável.

---

## 5, 6 e 7. Execução única

Não há Inbox neste consumidor, e a razão é melhor do que a alternativa: o
próprio registro da execução responde "isto já foi tratado?", e com mais
precisão. O status diz se terminou; o token de versão arbitra dois workers.

| Cenário | Resultado |
|---|---|
| Duas mensagens, dois workers em paralelo | **1** conclusão; quem perde o token sai sem escrever |
| Reentrega depois de concluída | encontra `Concluida`, apaga a mensagem, não reescreve |
| Cancelamento antes da execução | worker roda e não faz nada; sem resultado |
| Cancelamento com versão velha | `409` `versao_desatualizada` |
| Cancelamento depois de concluída | `409` `estado_do_backtest` |

**Cancelar não envia sinal nenhum ao worker.** Quem cancela sobe a versão da
linha; o worker que estiver executando tenta concluir com a versão que leu e o
banco recusa. Isso fecha a janela em que um worker "quase lá" gravaria o
resultado depois do cancelamento.

---

## 8. Mensagem que não é para este consumidor

O envelope é dado, nunca autoridade — a mesma disciplina do worker operacional
desde a Fase 5.

| Mensagem | Resultado |
|---|---|
| Corpo que não é JSON | recusada, devolvida para redrive |
| Tipo diferente na fila de backtests | recusada, devolvida para redrive |
| Tenant declarado ≠ tenant da execução | recusada, execução fica `Pendente` |
| Execução inexistente | recusada; após `MaximoDeRecebimentos`, vai para a fila de mortas |

Não se apaga o que não se entende: a mensagem circula até o redrive levá-la à
DLQ, onde fica para análise.

---

## 9. Produção intacta

Este é o risco mais sutil da fase, e o teste é o mais direto: as contagens de
**transações, avaliações, sinais, alertas, casos, versões de regra, versões de
perfil e vereditos** são lidas do banco antes e depois de uma execução
completa, e comparadas.

A Outbox é a única exceção, e ela é explicada: o pedido grava **um** evento, que
é o gatilho do próprio trabalho. O teste afirma exatamente isso — `+1` de
`BacktestSolicitado.v1` e **zero** de `TransacaoAvaliada.v1`. Um evento
operacional a mais viraria alerta no ciclo seguinte, e a fila do analista
receberia dado de ensaio.

Um segundo teste fecha o cerco pelo lado do comportamento: um candidato severo
o bastante para bloquear transações roda até o fim, o ciclo operacional roda
depois, e a contagem de alertas não muda.

O rascunho também continua rascunho depois da simulação — simular não publica.

---

## Fora do `ROADMAP.md`, mas do mesmo gate

| Verificação | Resultado |
|---|---|
| Analista e Auditor em qualquer rota de backtest | `403` |
| Sem token | `401` |
| Credencial de integração (`ApiKey`) numa rota humana | `401` |
| Listagem de A não enxerga execução de B | ✅ conferido nos dois sentidos |
| Vocabulário fechado no `jsonb` do candidato | tipo ou origem desconhecida falha alto, nunca vira membro inexistente |
| Não existe um segundo motor | teste de arquitetura: um único tipo avalia transação contra perfil |

---

## O que este gate **não** cobre

Nomeado para não ser confundido com cobertura:

- **Sem rate limit próprio** nas rotas de backtest. A defesa é o limite de
  execuções simultâneas, que é mais preciso — ele mede o trabalho, e não o
  número de requisições. Rate limiting distribuído é a Fase 11.
- **Sem limite de retenção.** Uma execução concluída fica para sempre, como as
  demais tabelas do produto.
- **Sem verificação de tempo real de execução sob carga.** O teto de 300 s é
  configuração, e o caminho de estouro é testado por unidade — não por uma
  execução que de fato demore isso.
