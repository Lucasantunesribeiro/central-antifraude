# ADR 0015 — Console operacional, painel e auditoria

- **Status:** aceito
- **Data:** 2026-09-07
- **Fase:** 10 — Operação, Busca, Painel e Auditoria

## Contexto

Até a Fase 9 o produto tinha todas as capacidades e nenhuma **experiência**. As
telas existiam porque cada fase precisava provar a sua parte: a lista de
transações não filtrava, o painel era um cartão de sessão, e a trilha de
auditoria era gravada há nove fases sem que existisse forma de lê-la.

O objetivo desta fase é o do ROADMAP 10.1:

> a Central Antifraude funciona como console operacional completo, não apenas
> coleção de endpoints.

A superfície nova é **consulta** — filtro livre, ordenação escolhida pelo
cliente, paginação e agregação. Nenhuma delas escreve nada, e é por isso que
são perigosas: uma consulta que vaza não deixa rastro no dado, só no que
alguém viu.

Três problemas novos, e cada decisão abaixo responde a um deles:

1. **texto do cliente virando consulta** — filtro, busca e campo de ordenação
   chegam como texto e precisam parar antes do SQL;
2. **dois números para a mesma pergunta** — agregar cria a tentação de
   ter uma fonte rápida e uma correta;
3. **a trilha na mão errada** — auditoria é um controle sobre quem opera.

---

## Decisão 1 — Filtrar, ordenar e paginar são a mesma consulta

A listagem de transações fazia duas consultas: as transações, e depois as
avaliações em lote. Isso bastava enquanto não havia filtro.

Filtrar por score, decisão ou tipo de sinal exige a junção. Mantê-las separadas
obrigaria a paginar sobre um conjunto que ainda seria filtrado depois: **o total
ficaria errado e páginas viriam pela metade** — o tipo de defeito que ninguém
reporta e todo mundo desconfia.

A junção é **à esquerda** por padrão: transações registradas na Fase 2 existem
sem avaliação, e sumi-las da lista esconderia dado real. Quando o filtro fala de
score, decisão ou sinal, ela vira efetiva — filtrar por score exclui, por
definição, o que não tem score.

Duas armadilhas do EF Core apareceram aqui e ficam registradas:

- **`AutoInclude` dentro de junção à esquerda não tem tradução.** A avaliação
  traz os sinais por `AutoInclude`, e uma coleção dentro de um `LEFT JOIN` faz
  o EF recusar a consulta inteira. A listagem não mostra sinal nenhum:
  `IgnoreAutoIncludes` não é otimização, é a condição para a consulta existir.
- **O par intermediário precisa ser um tipo anônimo.** O EF enxerga através
  dele para ordenar depois da junção, mas não enxerga através de um `record`
  posicional: com `new Linha(a, b).Transacao.RecebidaEm` no `ORDER BY`, a
  consulta deixa de ser traduzível.

---

## Decisão 2 — A busca livre é literal

É a única entrada de texto que chega ao banco. O EF parametriza, então não há
injeção de SQL — mas `%` e `_` continuariam sendo **operadores** do `LIKE`.

Um `%` digitado por engano devolveria a tabela inteira e a tela pareceria
filtrada. Isso é pior do que um erro: uma lista que volta cheia não levanta
suspeita, e quem consultou acredita ter filtrado.

O escape acontece em `FiltroDeTransacoes.PadraoDeBusca`, e não no repositório,
para que exista um teste unitário dizendo o que o produto promete: `%` e `_`
digitados pela pessoa são **texto**.

---

## Decisão 3 — O período filtra pela ocorrência, e não pela chegada

Quem investiga procura "o que aconteceu na terça". Uma transação atrasada
aconteceu na terça mesmo tendo chegado na quinta, e filtrar pela chegada a
esconderia justamente de quem foi procurá-la (`CLAUDE.md` seção 15).

O painel faz o **oposto**, e de propósito: ele conta decisões por `AvaliadaEm`.
Uma transação atrasada de três dias atrás foi decidida hoje, e é hoje que ela
entrou na fila do analista. Agrupar pela ocorrência faria o painel de hoje mudar
retroativamente sempre que um evento atrasado chegasse.

Os dois números aparecem lado a lado — *recebidas* e *avaliadas* — com a
explicação na tela. Igualá-los esconderia o comportamento que o produto existe
para tratar.

---

## Decisão 4 — O painel conta ao vivo, e a projeção diária continua sendo o que sempre foi

Isto **reverte a intenção original** registrada em `ResumoDiarioDeDecisoes` na
Fase 5 ("a Fase 10 usa esta tabela no painel operacional"), e a razão é
concreta.

A projeção é alimentada pelo caminho assíncrono. Quando a fila atrasa, ela fica
para trás. Um painel lendo dela discordaria da lista de transações — e duas
respostas para a mesma pergunta geram investigação sobre um problema que não
existe (`CLAUDE.md` seção 44).

O volume é o do envelope de portfólio (~10.000 avaliações/mês), então contar ao
vivo custa um `GROUP BY` sobre um índice que já existe. Otimizar antes de medir
seria o erro do outro lado (`CLAUDE.md` seção 116).

**A projeção mantém o papel que sempre teve**: ser o efeito assíncrono que prova
a idempotência do consumidor ponta a ponta — um contador, que é o pior caso para
entrega duplicada.

O que o painel expõe no lugar dela é algo que ela não responde: **quantos
eventos ainda não saíram da Outbox**. É a medida direta de fila parada, e um
número que não volta a zero significa alerta que não vai ser criado.

---

## Decisão 5 — Métricas de regra são contagens com denominador

O painel mostra, por regra: acionamentos no período, e o que a investigação
humana concluiu sobre as transações correspondentes.

| Regra | Acionamentos | Fraude | Legítima | Inconclusiva | Sem investigação |
|---|---|---|---|---|---|
| Velocidade por cliente | 9 | 2 | 1 | 0 | 6 |

**Nenhuma taxa é calculada, e a ausência é o ponto** (ROADMAP 10.5). As
transações com veredito não são amostra aleatória: elas viraram caso justamente
porque o motor as marcou. Uma regra com muitas fraudes confirmadas pode estar
apontando bem, ou pode ser a única que a equipe costuma investigar.

As quatro colunas somam exatamente os acionamentos — há teste para isso. É o que
torna a leitura honesta: cada acionamento cai em uma coluna, e o denominador
está à vista.

O nome vem da regra **atual**, e não da versão que produziu o sinal: renomear
não reescreve o passado, mas quem lê a métrica hoje precisa reconhecê-la pelo
nome de hoje (ADR 0013, decisão 6).

---

## Decisão 6 — A trilha é lida por quem audita, e por mais ninguém

Administrador e Auditor alcançam `/api/auditoria`. Supervisor e Analista, não.

A trilha é um controle **sobre** o que eles fazem — publicar regra, resolver
caso. Dar a quem é auditado o poder de varrer o próprio rastro enfraquece o
único registro que responde "quem fez o quê e quando" (`CLAUDE.md` seções 8.3 e
67).

A leitura ganhou **interface própria**, separada de `IRegistradorDeAuditoria`.
Aquela só escreve, e continuar assim deixa óbvio que nada no caminho de gravação
pode ler, alterar ou apagar o que já foi registrado.

**Não há ordenação configurável.** A trilha só suporta uma leitura — a sequência
dos fatos — e ordenar por outra coisa a desmontaria.

**Não há filtro por tipo de entidade.** A operação já diz sobre o que ela foi, e
um segundo vocabulário com os nomes das entidades sairia de sincronia na
primeira entidade nova, silenciosamente: o filtro deixaria de encontrar
registros que existem. `entidadeId` cobre a pergunta que realmente se faz numa
investigação — "tudo o que aconteceu com este caso".

O filtro de operação oferece **só o que existe na trilha do tenant**. Listar
trinta operações das quais quatro têm registro faz a pessoa procurar no vazio.

---

## Decisão 7 — A transação guarda o identificador de correlação

`Transacao.IdDeCorrelacao` é coluna nova, anulável.

O identificador já atravessava HTTP, Outbox, mensagem, worker e efeito
(`CLAUDE.md` seção 69). O que não havia era como **partir de uma transação na
tela e chegar à linha de log da requisição que a criou** — que é exatamente o
caminho útil numa investigação de suporte.

Anulável porque as transações gravadas antes desta fase não o têm, e inventar um
valor para elas seria pior do que admitir a ausência.

**Não entra no fingerprint do payload.** Ele identifica a requisição, e não o
conteúdo; incluir faria dois envios idênticos parecerem diferentes e quebraria a
idempotência. Num replay, a transação original é devolvida com a correlação
original — que é o correto: é ela que aponta para a requisição que de fato criou
o registro.

---

## Decisão 8 — O detalhe da transação reúne o que estava espalhado

A avaliação responde *por que esta decisão*. O que faltava era *alguém já olhou
isto* — e quem investiga precisa das duas coisas na mesma tela, senão volta a
procurar na outra aba.

O detalhe passou a trazer os alertas da transação com o caso que os recolheu, o
veredito humano quando existe, e o identificador de correlação.

Uma consulta só, e não três: atravessa alerta, caso e resultado de investigação,
e espalhar isso faria a tela de detalhe pagar três idas ao banco para responder
uma pergunta.

O veredito pode contradizer a decisão do motor — `Revisar` que termina
`Legítima`. É falso positivo legítimo, e a tela diz isso com todas as letras
(`CLAUDE.md` seções 11 e 94).

---

## Decisão 9 — Recusar, nunca corrigir em silêncio

| Entrada | O que acontece |
|---|---|
| Campo de ordenação fora da lista | `400` com os campos aceitos |
| Decisão ou tipo de regra fora do vocabulário | `400` |
| Faixa de score invertida ou fora de 0..100 | `400` |
| Busca com menos de 2 ou mais de 100 caracteres | `400` |
| Página ou tamanho fora da faixa | `400` |
| Janela do painel fora de 1..90 dias | `400` |

Reduzir em silêncio para o teto faria o cliente acreditar que recebeu a lista
inteira. Numa tela de fraude, acreditar que se viu tudo é pior do que receber um
erro — a mesma disciplina de paginação decidida na Fase 0.

O **número** também é recusado onde se espera um nome: `Enum.TryParse` aceitaria
`decisao=2` como `Revisar` e `decisao=99` como um valor que não existe no enum.
Comparar com os nomes fecha os dois buracos.

---

## Decisão 10 — Sem exportação

O ROADMAP 10.7 é explícito: *"Só implementar CSV/export se houver utilidade
concreta e escopo seguro. Não é obrigatória."*

Não entrou. Uma exportação adicionaria superfície de CSV injection e um caminho
de saída em massa de dados operacionais para resolver um problema que ninguém
tem hoje — a pergunta obrigatória do `CLAUDE.md` seção 124 responde sozinha.

Fica registrado como decisão, e não como esquecimento: o item correspondente do
Security Gate 10 é **não aplicável**, e não "pendente".

---

## Consequências

**Ganhos**

- O analista encontra uma transação específica sem sair da tela, e o link do
  filtro é compartilhável.
- O painel responde as perguntas do começo do expediente, com cada número
  atrelado a uma decisão possível.
- A trilha de auditoria, gravada desde a Fase 1, virou consultável — e continua
  sem nenhuma rota que a altere.
- O suporte tem um número de protocolo que liga a tela ao log.

**Custos aceitos**

- O painel dispara oito consultas agregadas por carregamento. São `count` e
  `GROUP BY` sobre índices existentes, no envelope de portfólio.
- A busca livre é `ILIKE '%termo%'`, que não usa índice. É busca de
  investigação, sobre o conjunto já restrito ao tenant.
- A projeção diária ficou sem leitor no produto. Ela continua sendo o efeito
  que prova a idempotência do consumidor, e isso já justifica a existência dela.

**Dívida registrada**

- Sem exportação, como decidido acima.
- Sem busca por texto em casos e notas — o console cobre transações.
- Sem filtro por integração no console: uma organização com muitas integrações
  vai querer isso, e nenhuma tem hoje.
