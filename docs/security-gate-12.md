# Security Gate 12 — observabilidade é uma superfície

Data da execução: **2026-09-07**
Escopo: a fase que **acrescenta** caminho para o dado sair.

Testes em `tests/CentralAntifraude.IntegrationTests/SecurityGate12Tests.cs`,
mais `ObservabilidadeTests` e `ResilienciaTests`, e os onze gates anteriores,
que continuam rodando.

Documentos: [`observabilidade.md`](observabilidade.md),
[ADR 0016](adr/0016-observabilidade-e-resiliencia.md).

---

## A premissa desta fase

Todas as fases anteriores perguntaram **quem pode chamar esta rota**. Esta
pergunta outra coisa:

> o que acabou de sair do banco sem passar por autorização nenhuma?

Log e métrica escapam do perímetro que o produto inteiro defende. O dado no
banco tem filtro de tenant, política de perfil e trilha de auditoria; a mesma
informação dentro de uma linha de log tem apenas as permissões do coletor. Um
termo de busca digitado por um analista, um identificador de cliente ou um score
copiado "só para facilitar o diagnóstico" atravessam essa fronteira sem que nada
reclame.

A Fase 12 é a única que cria caminho novo para o dado sair — e por isso precisa
de gate próprio, mesmo não tendo acrescentado **nenhuma rota**.

---

## O que foi verificado

| Verificação | Resultado |
|---|---|
| Termo digitado na busca do console aparece em algum log | **não** |
| Chave de integração ou access token em log | **não** |
| Corpo do evento (score, decisão, cliente) em log | **não** |
| Caminho concreto com identificador na propriedade `Operacao` | **não** — só o padrão da rota |
| Dimensão de métrica fora da lista de permissão | **não** |
| **Valor** de dimensão parecido com identificador | **não** |
| Medidor emitindo fora do catálogo | **não** |
| Correlação forjada pelo cliente injetando conteúdo no log | **não** — formato fechado |
| `/health/ready` com o banco fora vaza host, porta, base ou driver | **não** |
| A mesma falha aparece no **log** | **sim** |

---

## As três decisões de segurança desta fase

### 1. A operação é o padrão da rota, e nunca o caminho

`GET /api/casos/{id}`, e não `GET /api/casos/9f3c…`.

O argumento de cardinalidade é o conhecido — uma série de métrica por caso
investigado. O argumento de segurança é mais forte: o caminho concreto carrega
identificador de recurso de um tenant, e **métrica não tem tenant nem
autorização**. O mesmo raciocínio exclui a query string, que nesta API carrega o
termo de busca digitado por um analista.

Uma URL que não casa com rota nenhuma vira `desconhecida`. Sem isso, um varredor
que tenta mil caminhos criaria mil séries — a varredura sairia mais cara para
quem é varrido do que para quem varre.

### 2. As dimensões são lista de permissão, e os valores também são olhados

A lista de permissão defende o **nome** da dimensão. Mas alguém poderia declarar
`resultado` — nome impecável — e gravar ali um identificador de transação: o
efeito seria idêntico. Por isso há um segundo teste, sobre os **valores**
emitidos, que recusa qualquer coisa com forma de GUID ou ULID.

### 3. Não existe rota de métricas

Profundidade de Outbox e de fila são números do processo, e não de uma
organização: devolvê-los a um administrador de tenant contaria a ele o volume de
trabalho dos outros. A leitura acontece dentro do processo, por `MeterListener`,
e a Fase 14 conecta um exportador aos mesmos medidores.

Consequência direta: **a matriz de autorização da Fase 11 segue idêntica** — a
Fase 12 não acrescentou rota nenhuma para ela cobrir.

---

## As duas metades da mesma decisão

Com o banco fora:

| | Diz |
|---|---|
| Resposta de `/health/ready` | apenas `Unhealthy` e o nome do componente |
| Log | a falha de conexão, com o suficiente para consertar |

Trocar isso de lugar produz os dois erros clássicos ao mesmo tempo: um endpoint
de saúde que entrega topologia a quem perguntar, e uma equipe sem nenhuma pista
do que quebrou.

---

## O que este gate **não** cobre

Nomeado para não ser confundido com cobertura:

- **Retenção e acesso ao log em produção.** Quem pode ler os logs, por quanto
  tempo eles ficam e onde são armazenados é decisão da Fase 14, quando existir
  CloudWatch. O que está garantido aqui é o que **entra** na linha — e é
  deliberado que a garantia esteja nesta ponta: uma vez escrito, o dado depende
  de política de acesso que o produto não controla.
- **Sem exportador.** Os instrumentos existem no processo. Nenhum dado de
  métrica sai da aplicação ainda, o que também significa que a superfície de
  exportação ainda não foi analisada.
- **Sem teste de volume de log.** Uma linha por requisição é barata no envelope
  de portfólio; em volume alto, log é custo e é vetor de negação de serviço por
  disco. Não medido.
- **A amostragem de profundidade não tem limite de custo próprio** além do
  intervalo de 15 s. Se a tabela `mensagens_mortas` crescer muito, a contagem
  fica cara — hoje ela é vazia na operação normal.
