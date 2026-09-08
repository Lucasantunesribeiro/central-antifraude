# ADR 0016 — Observabilidade, resiliência e o fio que não pode arrebentar

- **Status:** aceito
- **Data:** 2026-09-07
- **Fase:** 12 — Observabilidade, Resiliência e Performance

## Contexto

Onze fases responderam se o produto **funciona**. Esta responde outra pergunta,
que só pode ser feita depois:

> quando algo der errado às três da manhã, existe como descobrir o que foi?

O ponto de partida não era zero. O `CorrelationId` existe desde a Fase 0 e
atravessa a Outbox, o envelope e a Inbox; há health checks separados desde a
Fase 0; três instrumentos de métrica foram criados nas Fases 4 e 6, cada um com
um comentário dizendo "para quem for coletar métricas na Fase 12". A DLQ existe
desde a Fase 5.

O que faltava eram quatro coisas específicas, e é útil nomeá-las porque só uma
delas é "adicionar log":

1. **o normal não era registrado.** O sistema logava exceções, recusas de
   credencial e ciclos de fundo — tudo o que dá errado. Quantas requisições
   existem, quais rotas e quanto demoram, não. Sem isso, *"a API está lenta"*
   não tem como ser respondido, e uma degradação só aparece quando vira erro;
2. **o fio arrebentava no meio.** O identificador atravessava o banco, mas as
   linhas de log do despachante e do worker nasciam sem ele. Uma investigação
   que seguisse um `CorrelationId` chegava até a publicação e recomeçava do
   zero do outro lado — exatamente na fronteira que é impossível reconstruir à
   mão depois;
3. **as métricas eram três, sem catálogo.** A pergunta *"quais métricas
   existem?"* só podia ser respondida lendo o código inteiro;
4. **a resiliência estava provada pela metade.** Havia teste para mensagem
   ruim — envenenada, duplicada, fora de ordem. Não havia para **dependência
   ausente**: banco fora, fila fora, efeito falhando, acúmulo represado,
   cliente que desiste.

---

## Decisão 1 — Uma linha e duas métricas por requisição

`MiddlewareDeTelemetria` registra `Operacao`, `Status` e `DuracaoEmMs` para
toda requisição, e alimenta `requisicoes_total` e `requisicao_duracao_ms`.

**A operação é o padrão da rota, e nunca o caminho.** `GET /api/casos/{id}`, e
não `GET /api/casos/9f3c…`. Isso vale como regra de cardinalidade — o caminho
concreto criaria uma série de métrica por recurso — mas o motivo mais forte é
outro: o caminho carrega identificador de recurso de um tenant, e métrica não
tem tenant nem autorização. O mesmo raciocínio exclui a query string, que nesta
API carrega o termo de busca digitado por um analista.

Uma URL que não casa com rota nenhuma vira `desconhecida`. Sem isso, um
varredor de vulnerabilidade que tenta mil caminhos criaria mil séries — a
varredura sairia mais cara para quem é varrido do que para quem varre.

**Saúde fica fora.** Um orquestrador consulta *liveness* a cada poucos segundos,
para sempre; incluir isso encheria o log de linhas que ninguém lê e daria à
distribuição de latência da API o formato do health check.

### O defeito que esta decisão revelou

O `UseExceptionHandler` **limpa o endpoint** antes de reexecutar o pipeline para
escrever a resposta de erro. Lendo apenas `HttpContext.GetEndpoint()`, toda
requisição terminada em erro de domínio — 404, 409, 400 — seria registrada como
`desconhecida`: justamente as requisições que alguém vai querer investigar
perderiam o nome da operação.

A leitura passou a ser `GetEndpoint() ?? IExceptionHandlerFeature.Endpoint`.
Isso foi encontrado por um teste que esperava `GET /api/casos/{id}` num 404 e
recebeu `desconhecida` — e não por revisão de código.

---

## Decisão 2 — A correlação é reposta na fronteira do assíncrono

`ContextoDeCorrelacaoMutavel` saiu da Api para a Application e passou a ser
registrado na composição da Infrastructure. O consumidor de eventos e o de
backtests o preenchem com o valor que veio no envelope, e abrem um escopo de log
com `CorrelationId`, `EventId` e `EventType`.

O despachante faz o mesmo por evento, e passou a registrar uma linha por
publicação — não só o resumo do ciclo. É ela que fecha o vão entre *"a
requisição respondeu"* e *"o worker agiu"*.

**Ler é contrato, definir é escrita.** Quem consome depende de
`IContextoDeCorrelacao`, que só lê. Apenas as duas bordas que sabem de onde vem
a correlação dependem da classe concreta.

O teste que prova isso lê **log**, e não banco: o banco já provava esta cadeia
desde a Fase 5, e o que faltava era exatamente o que ele não pode mostrar.

---

## Decisão 3 — Catálogo de métricas com lista de dimensões *permitidas*

`Telemetria` reúne os cinco medidores e os nomes dos instrumentos, e declara as
**dimensões permitidas**: `decisao`, `prioridade`, `resultado`, `operacao`,
`fila`, `laco`.

Uma lista de dimensões *proibidas* envelhece mal: ela cobre o que alguém lembrou
de proibir, e a dimensão que explode a cardinalidade é sempre a que ninguém
imaginou. Com permissão explícita, uma métrica nova que carregue dimensão não
prevista quebra a build, e quem a criou precisa declarar quantos valores ela
pode ter. **O risco fechado não é uma métrica cara que existe hoje; é a
próxima** — a mesma forma da matriz de autorização da Fase 11.

### Contadores, histogramas e medidores de estado

A separação não é estilística:

| Tipo | Custa | Exemplos |
|---|---|---|
| Contador | nada — o código que faz a coisa incrementa | publicados, consumidos, mortas |
| Histograma | nada — mede o que já estava acontecendo | duração de avaliação, de requisição |
| Estado | **uma consulta** | profundidade de Outbox e de fila, idade |

Ninguém "faz" uma fila ter 400 mensagens: é preciso ir perguntar ao banco. Por
isso `AmostradorDeIndicadores` é limitado por **tempo** (15 s por padrão), e não
por ciclo — os laços rodam a cada 100 ms quando há trabalho, e quatro contagens
por ciclo seriam dezenas de consultas por segundo para responder uma pergunta
que muda em minutos.

**A métrica que mais importa é a idade, não a contagem.** Uma Outbox com 300
eventos pendentes pode ser um pico normal; com 3 eventos pendentes há quarenta
minutos, é um despachante parado. A contagem sozinha não distingue os dois, e o
segundo é o que faz o analista esperar por um alerta que nunca chega.

---

## Decisão 4 — Não existe rota de métricas

A alternativa óbvia seria um endpoint devolvendo os números. Foi recusada por
dois motivos:

1. **profundidade de Outbox e de fila são números do processo, não de uma
   organização.** Não há como devolvê-los ao administrador de um tenant sem
   contar a ele o volume de trabalho dos outros;
2. **ela não é necessária.** Os testes rodam no mesmo processo que a API e
   escutam os instrumentos por `MeterListener`; a Fase 14 pluga um exportador
   nos mesmos medidores. Uma superfície HTTP a mais não provaria nada que isso
   não prove, e teria que ser autorizada, limitada e defendida.

A Fase 12, portanto, **não acrescenta nenhuma rota** — e a matriz de autorização
da Fase 11 segue idêntica.

---

## Decisão 5 — Resiliência é sobre dependência ausente

`ResilienciaTests` cobre cinco situações em que o sistema não recebe erro
nenhum de dado — simplesmente não consegue continuar:

| Situação | O que se exige |
|---|---|
| Banco fora | `/health/live` continua 200; `/health/ready` cai, sem vazar topologia |
| Fila fora | evento continua pendente e sai no ciclo seguinte, sem intervenção |
| Efeito falhando | mensagem volta e **a marca da Inbox não fica** |
| Acúmulo represado | drenado em lotes, sem perda e sem duplicata |
| Cliente que desiste | nenhuma transação gravada sem avaliação |

O terceiro é o menos óbvio e o mais grave: se a marca da Inbox ficasse gravada
apesar da falha do efeito, a reentrega encontraria "já processado" e o efeito
**nunca** aconteceria — o pior desfecho possível, porque se parece com sucesso.

O que essa classe deliberadamente **não** repete: mensagem envenenada, entrega
duplicada, worker caindo depois do efeito, despachantes concorrentes e retry de
serialização já têm teste desde as Fases 4 e 5.

---

## Consequências

**Positivas.** Um incidente pode ser seguido de ponta a ponta por um único
identificador, inclusive através do processo de fundo. A degradação passa a ter
número antes de virar erro. Uma métrica com dimensão cara não chega ao
repositório. As cinco formas de dependência ausente têm comportamento
verificado.

**Custo aceito.** Uma linha de log por requisição e uma amostra de profundidade
a cada 15 segundos. No envelope de portfólio — ~10.000 avaliações/mês — isso é
irrelevante; em volume maior, o intervalo de amostragem é configuração.

**Limite conhecido.** Nada disto é exportado ainda: os instrumentos existem no
processo e são lidos pelos testes. O exportador é da Fase 14, e ele se conecta
aos mesmos medidores sem tocar em nenhum ponto de chamada.

**O que ficou sabido e não resolvido.** Sob `SERIALIZABLE`, com a tabela
`transacoes` pequena, o planejador escolhe varredura sequencial e o predicado
passa a cobrir a relação inteira — clientes independentes entram em contenção
artificial. Está medido, explicado pelo plano de execução e registrado em
[`baseline-de-performance.md`](../baseline-de-performance.md); a mitigação da
Fase 4 (`SET LOCAL cpu_tuple_cost`) reduz, mas só o volume elimina.
