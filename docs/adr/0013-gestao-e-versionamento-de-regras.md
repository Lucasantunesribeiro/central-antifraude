# ADR 0013 — Gestão e versionamento de regras

- **Status:** aceito
- **Data:** 2026-09-06
- **Fase:** 8 — Gestão e Versionamento de Regras

## Contexto

Até a Fase 7 o catálogo de regras era provisionado junto da organização e não
havia rota que o alterasse. O motor executava sempre a mesma configuração.

Esta fase abre a administração: o Supervisor cria regras, ajusta configuração e
peso, publica versões e mexe nos limiares.

Isso muda a natureza do risco. Nas fases anteriores uma pessoa alterava **dados
operacionais** — um caso, uma nota, um veredito. Agora ela altera o
**comportamento do produto**: uma regra publicada alcança toda transação que
entrar depois.

Três problemas novos, e cada decisão abaixo responde a um deles:

1. **o passado tem que continuar explicável** enquanto o presente muda;
2. **configurar não pode virar programar** — o catálogo é fechado;
3. **duas pessoas mudando o motor ao mesmo tempo** não podem se sobrescrever.

---

## Decisão 1 — A regra é mutável; a versão publicada, nunca

A separação já existia desde a Fase 3 e agora ganha o rascunho:

| Objeto | Muda? | O que carrega |
|---|---|---|
| `Regra` | sim | identidade, nome, ativação, **rascunho**, token de versão |
| `VersaoDeRegra` | **não** | configuração e peso congelados |
| `VersaoDePerfilDeRisco` | **não** | limiares e o conjunto de versões de regra |

```text
Rascunho  →  Publicada (v1)  →  novo rascunho  →  Publicada (v2)
```

Publicar congela o rascunho numa versão e esvazia o rascunho. As versões
anteriores continuam existindo, e é por elas que uma avaliação antiga é
explicada (`CLAUDE.md` seções 17, 23 e 24).

**A imutabilidade não é convenção.** `VersaoDeRegra` e `VersaoDePerfilDeRisco`
não têm método de alteração, não têm setter público e não têm rota. Um teste de
arquitetura verifica que continua assim — ele existe para o dia em que alguém,
resolvendo um problema legítimo, adicionar um `AjustarPontos`.

---

## Decisão 2 — Nada muda o motor a não ser publicar uma versão de perfil

Quatro operações mudam o que o motor executa, e **as quatro terminam publicando
uma versão nova de perfil**, na mesma transação:

| Operação | Efeito no perfil |
|---|---|
| Publicar uma regra | versão nova, com a versão nova da regra no lugar da anterior |
| Desativar uma regra | versão nova, sem ela |
| Reativar uma regra | versão nova, com a última versão publicada dela |
| Ajustar limiares | versão nova, com os limiares novos |

O motivo é que a alternativa cria um estado intermediário indefensável: uma
versão de regra "publicada" que o motor não executa. A tela teria que explicar a
diferença entre publicada e vigente, e o Supervisor teria que lembrar de um
segundo passo para a mudança valer.

Com esta decisão, **a versão de perfil mais recente é uma descrição fiel do que
o motor está executando** — que é exatamente o que a tela de regras mostra.

Um efeito colateral bom: a composição do perfil é sempre derivada, nunca
editada. "Quais regras estão no perfil" é a resposta a uma pergunta — as ativas
que já foram publicadas —, e não um campo que alguém preenche e pode errar.

---

## Decisão 3 — Um perfil publicado precisa de ao menos uma regra

Desativar a última regra do perfil é recusado.

Um perfil sem regra pontuaria toda transação com zero e recomendaria `Permitir`
para tudo. Não daria erro, não apareceria em log nenhum: o motor simplesmente
ficaria cego, e a operação descobriria isso pelo silêncio da fila de alertas.

---

## Decisão 4 — Duas regras do mesmo tipo passam a ser permitidas

Até aqui havia um índice único `(organização, tipo)`: uma regra por tipo.

O ROADMAP 8.2 exige "criar regra", e toda organização nasce com os quatro tipos
provisionados — com a restrição antiga, a criação seria uma rota morta.

Além disso, duas velocidades com janelas diferentes — "3 em 10 minutos" e "20 em
12 horas" — são configurações legítimas e diferentes, não duplicação.

**O que substitui a restrição:** o nome passa a ser único por organização. Com
o tipo deixando de distinguir, o nome é o que resta para a lista, a trilha de
auditoria e a explicação de um sinal não ficarem ambíguas.

**O que a mudança exigiu:** a ordem de execução do motor era `OrderBy(Tipo)`.
Com duas regras do mesmo tipo, isso deixou de ser uma ordem total, e a ordem
entre elas passaria a depender do banco — o motor deixaria de ser determinístico
na ordem dos sinais. Agora é `OrderBy(Tipo).ThenBy(RegraId)`, e a mesma correção
foi aplicada aos três outros pontos que ordenam sinais.

---

## Decisão 5 — Configurar é escolher números, nunca escrever lógica

A API não recebe expressão, SQL nem script. Recebe um tipo do catálogo e um
dicionário de **números nomeados**, e um `switch` fechado
(`CatalogoDeTiposDeRegra.TentarMontar`) constrói o record tipado.

Três recusas explícitas, todas `400`:

| Recusa | Por quê |
|---|---|
| campo desconhecido | aceitar faria quem enviou acreditar que o número foi usado pelo motor |
| campo faltando | um padrão escolhido pelo servidor mudaria o comportamento sem ninguém ter decidido |
| valor fora da faixa | com a faixa na mensagem, para o Supervisor saber qual campo corrigir |

**A tela monta o formulário a partir de `/api/regras/tipos`.** Rótulo, faixa,
valor padrão e se o campo aceita casa decimal vêm do servidor. Repetir os
limites no frontend criaria uma segunda lista que sairia de sincronia no
primeiro ajuste — e a tela passaria a aceitar o que o domínio recusa.

A faixa aparece em dois lugares no backend: na descrição do campo e dentro de
`ConfiguracaoDeRegra.Validar`, que é a autoridade. Um teste percorre cada campo
de cada tipo e exige que um passo além de cada extremo seja recusado e que cada
extremo seja aceito — a duplicação vira contrato verificado.

---

## Decisão 6 — O nome é rótulo da identidade, e não parte da versão

Renomear uma regra vale imediatamente e não cria versão.

Isso não perde explicabilidade histórica: a frase que explica cada sinal já foi
congelada no próprio sinal no momento da avaliação
(`SinalDeRisco.Explicacao`, ADR 0008). O nome serve para a equipe reconhecer a
regra hoje.

Peso e configuração são de outra natureza — mudam o que o motor faz — e por isso
só passam a valer com publicação.

---

## Decisão 7 — Publicar um rascunho igual à versão em vigor é recusado

Uma v2 idêntica à v1 faria o histórico de versões afirmar uma mudança que não
houve. É esse histórico que a Fase 9 vai usar para comparar regras candidatas, e
uma versão sem diferença nenhuma seria ruído numa comparação.

---

## Decisão 8 — Concorrência administrativa em duas camadas

Igual aos casos (ADR 0012), e pelo mesmo motivo — as duas fazem falta:

| Camada | O que ela pega |
|---|---|
| `versao` da regra enviada pelo cliente, conferida na aplicação | o caso comum: alguém agindo sobre uma tela velha. `409` sem tocar no banco |
| `versao` como token de concorrência do EF | duas requisições que passam **juntas** pela primeira checagem |
| índices únicos `(regra, número)` e `(perfil, número)` | duas publicações que calculam o mesmo próximo número |

O terceiro item é específico desta fase. Duas publicações de **regras
diferentes** são as duas legítimas — cada uma carrega a versão correta da sua
regra —, mas calculam o mesmo próximo número de versão de perfil a partir da
mesma vigente. Quem perde a corrida esbarra no índice único e recebe `409`
`conflito_de_publicacao`.

Sem essa camada, as duas gravariam: a versão vigente do perfil dependeria de
quem terminou por último, e a outra regra ficaria publicada e **fora** do perfil
em vigor, sem ninguém perceber.

---

## Decisão 9 — Rotas de ação, e não um `PUT` no recurso

Não existe `PUT /api/regras/{id}`. Um corpo com o objeto inteiro convidaria ao
mass assignment que o produto recusa: bastaria mandar `ativa` ou
`numeroDaUltimaVersao`.

| Rota | Quem |
|---|---|
| `GET /api/regras`, `GET /api/regras/perfil` | qualquer perfil |
| `GET /api/regras/tipos`, `/gestao`, `/{id}` | supervisão |
| `POST /api/regras` | supervisão — cria como rascunho |
| `PUT /api/regras/{id}/rascunho` | supervisão |
| `DELETE /api/regras/{id}/rascunho?versao=N` | supervisão |
| `POST /api/regras/{id}/publicacao` | supervisão |
| `POST /api/regras/{id}/ativacao` | supervisão |
| `POST /api/regras/perfil/limiares` | supervisão |

**A leitura do catálogo continua aberta a todos os perfis, inclusive ao
Auditor.** O analista precisa das regras vigentes para entender o próprio score;
fechar a leitura junto com a escrita teria sido simples e errado.

---

## Decisão 10 — O rascunho mora na regra

`ConfiguracaoEmRascunho` e `PontosEmRascunho` são colunas da tabela `regras`, e
não uma tabela própria.

O rascunho é um **estado da regra** — no máximo um por vez, sem histórico e sem
vida própria. Uma tabela separada exigiria um join em toda leitura
administrativa só para responder "tem rascunho?".

---

## Consequências

**Ganhos**

- O Supervisor administra o motor sem que nenhuma avaliação passada mude.
- "O que está valendo" tem uma resposta única e verificável: a última versão de
  perfil publicada.
- A Fase 9 recebe um histórico de versões que significa alguma coisa — cada
  versão representa uma mudança real.
- O catálogo continua fechado: nada que venha do cliente vira comportamento.

**Custos aceitos**

- Uma regra desativada e reativada gera duas versões de perfil. É ruído no
  histórico do perfil, mas é o preço de ter uma única forma de mudar o motor.
- Sem backtest antes de publicar. O `CLAUDE.md` seção 23 coloca o backtest
  entre rascunho e publicação, e ele é a Fase 9 — até lá o rascunho é a única
  etapa de revisão.
- A migration que troca o índice de `(organização, tipo)` para
  `(organização, nome)` não é reversível depois que existir uma segunda regra
  de um mesmo tipo. O `Down` está escrito e documenta isso.

**Dívida registrada**

- Sem simulação do impacto antes de publicar — é exatamente o que a Fase 9 traz.
- Sem comparação lado a lado entre duas versões na tela; o histórico mostra cada
  uma, e a diferença fica por conta de quem lê.
- Sem exclusão de regra. Desativar é o caminho, e é o correto: excluir apagaria
  a referência das versões que explicam avaliações antigas.
