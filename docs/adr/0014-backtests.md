# ADR 0014 — Backtests

- **Status:** aceito
- **Data:** 2026-09-07
- **Fase:** 9 — Backtests

## Contexto

A Fase 8 abriu a administração de regras e fechou com uma dívida registrada:

> Sem backtest antes de publicar. O `CLAUDE.md` seção 23 coloca o backtest
> entre rascunho e publicação, e ele é a Fase 9 — até lá o rascunho é a única
> etapa de revisão.

Esta fase paga essa dívida. O problema é concreto: publicar uma regra alcança
**toda transação que entrar depois**, e hoje o Supervisor decide no escuro. Ele
não sabe se a mudança geraria dez alertas ou dez mil, nem se pegaria as fraudes
que a equipe já confirmou.

Três problemas novos, e cada decisão abaixo responde a um deles:

1. **medir sem mentir** — o backtest só vale se rodar o motor de verdade;
2. **medir sem estragar** — simulação não pode virar dado operacional;
3. **medir sem custar** — a leitura é grande e o gatilho é um clique.

---

## Decisão 1 — O candidato é um perfil, e não uma regra solta

A Fase 8 estabeleceu que **uma única coisa muda o que o motor executa: publicar
uma versão de perfil** (ADR 0013, decisão 2). O backtest herda isso: o que se
simula é a **versão de perfil que seria publicada**, e não uma regra fora de
contexto.

Na prática, o pedido diz "simule o rascunho da regra X" e o servidor compõe o
candidato com a mesma regra de composição da publicação — última versão
publicada de cada regra ativa, na ordem tipo/regra, com o rascunho de X no
lugar dela.

Simular uma regra isolada responderia a uma pergunta que o produto não permite
fazer: não existe caminho para publicar uma regra sem suceder o perfil.

Um efeito colateral bom: o candidato passa pelas **mesmas invariantes** de uma
versão de perfil real — ao menos uma regra, limiares coerentes, sem regra
repetida. Simular o que não poderia ser publicado descreveria um estado
inalcançável.

---

## Decisão 2 — Não existe um segundo motor

O ROADMAP 9.4 é explícito: nada de `BacktestRiskEngine`. O que muda em relação
à produção são duas coisas, e nenhuma delas é a semântica das regras:

| | Produção | Backtest |
|---|---|---|
| De onde vem o perfil | versão publicada, lida do banco | candidato montado em memória |
| De onde vem o contexto | uma consulta por avaliação | o passado inteiro lido uma vez e recortado |

Para que o candidato chegue ao motor sem existir no banco, `VersaoDeRegra` e
`VersaoDePerfilDeRisco` ganharam fábricas `ParaSimulacao` — internas, com
número zero, e nunca adicionadas ao contexto de persistência. A alternativa
seria montar entidades descartáveis com `Publicar`, o que daria identificadores
sorteados e faria a **ordem de execução** do candidato divergir da do perfil
vigente que está sendo comparado.

**Que o recorte do contexto é fiel não é promessa: é teste.** Uma execução cujo
candidato é igual ao perfil vigente precisa reproduzir, decisão a decisão, as
avaliações que ficaram gravadas na ingestão. Se a janela, o teto ou a ordem
divergissem, os números não fechariam.

---

## Decisão 3 — Comparar candidato contra vigente, e não contra o passado gravado

Os dois lados são calculados na **mesma passagem**, sobre o mesmo contexto.

Comparar o candidato com a avaliação que ficou gravada seria mais barato e
enganoso: dentro de uma janela de noventa dias o perfil pode ter sido publicado
três vezes, e "quantas decisões mudariam" passaria a misturar o efeito do
candidato com o efeito de mudanças que já foram feitas.

A base de comparação é a **versão de perfil que valia quando a pergunta foi
feita**, congelada no registro da execução. Uma publicação entre o pedido e a
execução não desloca a régua.

---

## Decisão 4 — Tudo o que decide o resultado é congelado no pedido

O snapshot guarda o perfil candidato inteiro, a versão de perfil de comparação
e a janela.

O rascunho de uma regra muda a qualquer momento. Se o worker o lesse na hora de
executar, o resultado descreveria uma configuração diferente da que foi pedida
— e ninguém saberia disso olhando a tela.

Por isso a mensagem da fila carrega **apenas o identificador da execução**.
Repetir o candidato nela criaria duas fontes para o mesmo fato, e a mensagem —
que pode ser reentregue horas depois — seria a menos confiável das duas.

---

## Decisão 5 — Contagens com denominador, nunca precisão nem recall

O resultado cruza o veredito humano com o que cada perfil decidiria:

```text
Fraude confirmada   3   → candidato: 0 permitir, 0 revisar, 3 bloquear
Legítima            8   → candidato: 6 permitir, 2 revisar, 0 bloquear
Inconclusiva        1   → ...
Sem investigação  108   → ...
```

**Isso não é taxa de acerto.** A maioria das transações nunca foi investigada,
e as que foram não são amostra aleatória — viraram caso justamente porque o
motor as marcou. Chamar `3/3` de "taxa de detecção" seria uma afirmação que os
dados não sustentam (`CLAUDE.md` seções 22 e 112, ROADMAP 9.6).

"Sem investigação" é uma linha própria, distinta de "inconclusiva": uma é
ausência de análise, a outra é análise que não concluiu. Confundi-las inflaria
o denominador do que a equipe realmente apurou.

A distribuição de score usa cinco faixas fixas de vinte pontos, que **não**
acompanham os limiares — os limiares mudam entre o vigente e o candidato, e
faixas que mudassem junto tornariam as duas colunas incomparáveis.

---

## Decisão 6 — Assíncrono, em fila separada, pela Outbox

```text
POST  →  execução + evento na MESMA transação  →  despachante  →  fila de backtests  →  worker
```

**Fila separada** (`CLAUDE.md` seção 30): backtest é trabalho longo e em lote;
alerta é trabalho curto e operacional. Na mesma fila, uma execução de cinco mil
transações ficaria na frente do alerta de uma transação bloqueada, e o analista
descobriria isso pela demora.

**Pela Outbox**, e não por envio direto: gravar a execução e mandar a mensagem
são duas escritas em sistemas diferentes. Enviar direto abriria o dual-write que
a Fase 5 fechou — a mensagem sairia mesmo que o commit falhasse, e um worker
procuraria uma execução que nunca existiu.

O despachante passou a escolher a fila pelo **tipo do evento**. Um tipo novo cai
na fila operacional até que alguém decida o contrário: é a escolha
conservadora, porque a fila operacional tem consumidor e o consumidor recusa o
que não entende.

---

## Decisão 7 — O status da execução faz o papel da Inbox

O consumidor de backtests não grava `EventoProcessado`.

O que a Inbox faria — lembrar que o evento X já foi tratado — o próprio
registro da execução já faz, e com mais precisão: o status diz se o trabalho
terminou, e o token de versão arbitra dois workers que tentem terminá-lo ao
mesmo tempo. É a "invariante própria do efeito" que o `CLAUDE.md` seção 42 pede
ao lado da Inbox — só que aqui ela basta sozinha, porque o efeito é **uma única
linha**, e não um conjunto de gravações espalhadas.

Uma execução presa em `Executando` além do tempo máximo é **retomada** por
outro worker. Retomar é seguro precisamente porque um backtest não tem efeito
colateral: refazer o trabalho desperdiça tempo e nada mais. Se ele criasse
alerta ou avaliação, esta porta não poderia existir.

---

## Decisão 8 — Cancelar é subir o token de versão, e não mandar um sinal

Não há verificação de cancelamento dentro do laço.

Quem cancela sobe a versão da linha. O worker que estiver executando tenta
concluir com a versão que leu e o banco recusa; o resultado é descartado. Isso
fecha a janela em que um worker "quase lá" gravaria o resultado depois do
cancelamento — e evita uma consulta de verificação a cada transação analisada.

Cancelada vence falha: se as duas coisas acontecerem, o que fica registrado é a
decisão de gente.

---

## Decisão 9 — Recusar, nunca truncar

| Limite | Padrão | O que acontece ao estourar |
|---|---|---|
| Janela | 90 dias | `400` no pedido |
| Transações analisadas | 5.000 | `400` no pedido, `Falhou` no worker |
| Transações de contexto | 50.000 | `Falhou` no worker |
| Execuções em andamento por organização | 2 | `409` `limite_de_execucoes` |
| Tempo de execução | 300 s | `Falhou`, e a execução pode ser retomada |

Truncar devolveria um resultado que **parece completo** e descreve um pedaço
arbitrário do período. Uma decisão de publicar regra tomada sobre isso seria
pior do que não ter backtest nenhum. É a mesma disciplina da paginação, decidida
na Fase 0.

O limite de execuções simultâneas é **por organização**, e não global: um limite
global faria um cliente movimentado bloquear todos os outros — negação de
serviço entre tenants pela porta da frente.

---

## Decisão 10 — O contexto histórico é lido uma vez, com recuo

O provedor de produção carrega o passado de **uma** transação no caminho
crítico. Aqui são milhares, e repetir aquela consulta por transação
transformaria um backtest em cinco mil idas ao banco.

O passado é lido de uma vez e recortado em memória, com a mesma janela e o mesmo
teto de `OpcoesDeAvaliacao`. E a leitura **começa antes** da janela analisada:
cada transação precisa enxergar o mesmo passado que enxergou na avaliação real.
Sem esse recuo, as transações do início do período seriam avaliadas como se
fossem as primeiras do cliente, e as regras de histórico ficariam caladas
justamente onde deveriam falar.

---

## Consequências

**Ganhos**

- O Supervisor decide com número, e não com intuição, antes de publicar.
- O veredito humano da Fase 7 passa a ter uso: ele é a única verdade apurada
  que o produto tem.
- "O backtest usa o motor de verdade" virou uma afirmação verificável, e não
  uma promessa de documentação.
- A fila de backtests, criada na Fase 5 e nunca usada, ganhou consumidor.

**Custos aceitos**

- O contexto histórico é recortado em memória, e a fidelidade desse recorte
  depende de um teste — não do compilador. É por isso que o teste existe.
- Duas execuções do motor por transação. O custo é irrelevante perto da
  leitura, e a alternativa — comparar com a avaliação gravada — seria mais
  barata e enganosa.
- Backtest não pagina o histórico: ele carrega o período inteiro. Acima de
  50.000 transações de contexto, a execução falha em vez de degradar.

**Dívida registrada**

- Sem comparação entre duas execuções na tela. Cada uma é lida por si.
- Sem simulação de perfil inteiro escrito à mão: o candidato sempre nasce do
  rascunho de uma regra ou de um ajuste de limiares.
- Uma execução não expira e não é apagada; a tabela cresce sem teto, como as
  demais do produto.
