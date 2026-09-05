# Security Gate 6 — Alertas operacionais

Data da execução: **2026-09-05**
Escopo: a fila de trabalho do analista.

Testes em `tests/CentralAntifraude.IntegrationTests/SecurityGate6Tests.cs` e
`AlertasTests.cs`.

---

## A premissa desta fase

A superfície nova é pequena: **uma rota de leitura**. O que este gate precisa
provar, então, é sobretudo **o que não existe** — e ausência é a coisa mais
fácil de prometer e mais fácil de esquecer de verificar.

O resto é o de sempre com uma tela nova: um tenant não vê a fila do outro,
filtro forjado é recusado em vez de ignorado, e o alerta não leva para a tela
nada que o produto escolheu não expor.

---

## Itens exigidos pelo `ROADMAP.md` (seção 6.7)

| # | Item | Resultado | Teste |
|---|---|---|---|
| 1 | Alerta cross-tenant | ✅ cada tenant vê só o próprio | `Um_tenant_nunca_ve_o_alerta_do_outro` |
| 2 | Filtros manipulados | ✅ 11 formas, todas `400` | `Filtro_forjado_e_recusado_e_nao_ignorado` |
| 3 | Operações em lote | ✅ não existem | `Nenhum_perfil_altera_alerta_porque_nao_existe_rota_de_escrita` |
| 4 | Status interno protegido | ✅ nenhuma rota o alcança | idem |
| 5 | Evento duplicado não duplica alerta | ✅ duas camadas, testadas isoladas | `Evento_entregue_de_novo_nao_duplica_o_alerta` e `Sem_a_marca_da_inbox_a_restricao_do_banco_ainda_segura_o_alerta` |
| 6 | Auditor não altera alerta | ✅ ninguém altera | `Nenhum_perfil_altera_alerta...` |
| 7 | Analista não altera regra | ✅ catálogo segue sem escrita | `O_catalogo_de_regras_continua_sem_rota_de_escrita` |

---

## 1 e 4. Isolamento

Dois tenants, um alerta em cada, gerados pelo caminho completo — ingestão,
Outbox, fila, worker.

| Cenário | Resultado |
|---|---|
| Analista do tenant A lista a fila | vê **um** alerta, e é o dele |
| Analista do tenant B lista a fila | vê **um** alerta, e é o outro |
| `?organizacaoId=`, `?tenantId=`, `?organizacao=` do outro tenant | lista **vazia** nas três |
| Sem autenticação | `401` |
| Com a **chave de integração** no lugar do token humano | `401` |

O tenant vem da identidade autenticada, e o filtro global do `DbContext` é
quem o aplica — não um `Where` que alguém precisa lembrar de escrever.

A verificação do isolamento não para na resposta HTTP: o teste confere **no
banco** que os dois alertas pertencem a organizações diferentes. Uma consulta
que devolvesse um alerta só por engano de dados passaria no teste de HTTP.

---

## 2. Onze filtros forjados

Todos devolvem `400`, e nenhum devolve a fila sem filtro:

| Caso | Caso |
|---|---|
| `decisao=Inventada` | `decisao=2` (o número de `Revisar`) |
| `prioridade=Critica` | `prioridade=99` |
| `scoreMinimo=-1` | `scoreMinimo=101` |
| `ordenarPor=organizacao_id` | `ordenarPor=score;DROP TABLE alertas` |
| `direcao=aleatoria` | `tamanho=5000` |
| `pagina=0` | |

Os dois numéricos merecem destaque. `Enum.TryParse` sozinho aceitaria `2` como
`Revisar` e deixaria `99` atravessar como um valor que não existe no enum — por
isso a resolução compara com os **nomes**, e só então converte.

O corpo do erro é conferido: nada de `Npgsql`, `SELECT` ou `StackTrace`.

**Recusar, e nunca ignorar.** Um filtro descartado em silêncio devolveria a
fila inteira, e quem consultou acreditaria estar vendo só o que pediu. Numa
fila de fraude, acreditar que se filtrou é pior do que receber um erro.

---

## 3, 4 e 6. A ausência de escrita

Este é o teste central do gate. Sete tentativas, com token de **Administrador**
— o perfil mais forte do produto — e corpo tentando `status` e `prioridade`:

```text
POST   /api/alertas
PUT    /api/alertas/{id}
PATCH  /api/alertas/{id}
DELETE /api/alertas/{id}
POST   /api/alertas/{id}/resolver
POST   /api/alertas/{id}/atribuir
POST   /api/alertas/em-lote
```

Todas devolvem `404` ou `405`. E o alerta é lido de volta do banco depois:
continua `Aberto`, continua `Media`.

Alerta é resultado de avaliação. Abrir escrita criaria um caminho para inventar
trabalho de investigação a partir de um JSON — sem avaliação, sem sinais e sem
explicação. A ação humana sobre um alerta pertence ao **caso**, que é a Fase 7.

O item "Auditor não altera alerta" fica coberto por construção: **ninguém**
altera. O Auditor lê a fila como todo perfil autenticado — consultar decisões é
exatamente o trabalho dele (`CLAUDE.md` seção 8.3).

---

## 5. Evento duplicado, uma camada de cada vez

Duas camadas protegem o alerta, e cada teste **derruba uma** para provar que a
outra sozinha já segura:

| Teste | O que é derrubado | O que segura |
|---|---|---|
| `Evento_entregue_de_novo_nao_duplica_o_alerta` | nada — reentrega normal | a Inbox: `0 processados, 1 repetido` |
| `Sem_a_marca_da_inbox_a_restricao_do_banco_ainda_segura_o_alerta` | a marca da Inbox é apagada | `alertas.avaliacao_id` único: mesmo alerta, mesmo `Id`, mesmo `CriadoEm` |
| `Um_efeito_ja_aplicado_nao_impede_o_outro` | a marca do criador de alertas | o savepoint: o alerta volta, o contador **não** soma de novo |

Sem esses testes seria impossível saber qual das duas camadas está realmente
trabalhando — e uma proteção nunca exercitada é uma proteção que ninguém sabe
se existe.

---

## O que o alerta não leva para a tela

A fila mostra o que ajuda a priorizar. Verificado que a resposta **não contém**:

| Ausente | Por quê |
|---|---|
| `pi_demo…` — referência do instrumento | não ajuda a priorizar (`CLAUDE.md` 58) |
| `disp-novo`, `disp-de-casa` — fingerprint do dispositivo | idem |
| `203.0.113.…` — endereço IP | nunca sai do HMAC (`CLAUDE.md` 57) |
| os campos `referenciaDoInstrumento`, `fingerprint…`, `enderecoIp` | idem |

A busca é pelos **valores**, e não por palavras: o tipo de regra
`NovoDispositivo` aparece legitimamente entre os sinais — ele diz que o
aparelho era novo, sem dizer qual aparelho é.

---

## Achado desta execução

**A ordenação por prioridade estava alfabética, e o alfabeto é o inverso da
gravidade.** A coluna guarda texto (`"Alta"`, `"Media"`) para que uma consulta
manual durante uma investigação seja legível. Mas `"Alta" < "Media"` — então
pedir "mais grave primeiro" trazia os **menos graves** no topo.

Não é um erro que a tela denuncie: a lista pareceria perfeitamente ordenada, e
o analista trabalharia a fila na ordem errada sem nunca perceber. Corrigido com
uma expressão explícita de gravidade na consulta, com a coluna continuando
legível.

Encontrado pelo teste `A_fila_ordena_por_score_e_por_prioridade`, que afirma a
ordem esperada em vez de afirmar apenas "a lista veio ordenada".

---

## O que este gate **não** cobre

- **Rate limit na consulta da fila.** Não há operação cara nem enumerável ali
  hoje; a paginação já tem teto rígido. A Fase 11 revisita as superfícies.
- **Ação humana sobre alerta.** Não existe nesta fase — é a Fase 7 que a
  introduz, e é lá que autoria, autorização e trilha precisam ser testadas.
- **Retenção do alerta.** Alerta não expira e não é apagado. Política de
  retenção não é assunto da v1.
- **Autenticação da fila de mensagens.** Herdado do Security Gate 5: hoje é uma
  tabela no mesmo banco; com SQS entra política IAM, na Fase 14.

---

## Resultado

**Security Gate 6: verde.**

24 testes em `SecurityGate6Tests`, 14 em `AlertasTests`, 11 unitários de
política e 25 de filtro. Nenhum item do ROADMAP seção 6.7 ficou sem
verificação executável.
