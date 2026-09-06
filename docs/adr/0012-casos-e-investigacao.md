# ADR 0012 — Casos e investigação

- **Status:** aceito
- **Data:** 2026-09-05
- **Fase:** 7 — Casos e Investigação

## Contexto

A Fase 6 entregou uma fila de alertas que ninguém podia tocar. Esta fase abre
a escrita humana no produto pela primeira vez: título, nota, atribuição,
veredito.

Isso muda a natureza dos riscos. Até aqui os gates cuidavam de leitura e da
ausência de escrita. Agora existem três problemas novos: **texto escrito por
gente** (por onde XSS entra), **duas pessoas ao mesmo tempo** (por onde o lost
update entra) e **estado que não pode voltar atrás** — porque a Fase 9 vai usar
o resultado humano como verdade para comparar regras candidatas.

---

## Decisão 1 — Um caso nasce de alertas, e nunca vazio

O ROADMAP 7.3 diz que o caso é criado a partir de um ou mais alertas. A
invariante é do domínio: `Caso.Abrir` recusa lista vazia.

Um caso sem alerta não teria transação para investigar nem resultado para
registrar — seria um ticket, e o `CLAUDE.md` seção 12 recusa transformar o
produto nisso.

Os alertas são levados para o caso **dentro** de `Abrir`, e não pelo chamador.
Deixar isso fora abriria a possibilidade de um caso existir citando um alerta
que continuou aberto na fila de outra pessoa.

**Um alerta pertence a no máximo um caso.** Dois casos sobre o mesmo alerta
produziriam duas conclusões humanas sobre a mesma transação, e a Fase 9 não
teria como saber qual é a verdade.

---

## Decisão 2 — Estado e história mudam na mesma operação

Toda ação sobre o caso acrescenta uma entrada na timeline **no mesmo método de
domínio**. Não existe caminho que mude o estado sem registrar.

| Ação | Efeito | Timeline |
|---|---|---|
| Abrir | `Novo`, sem responsável | `CasoAberto` + um `AlertaAssociado` por alerta |
| Assumir | responsável = você; `Novo` → `EmAnalise` | `Atribuido` |
| Transferir | responsável = outra pessoa | `Transferido` |
| Anotar | nota nova | `NotaAdicionada` |
| Resolver | `Resolvido` + resultado + vereditos | `Resolvido` |

`Novo → EmAnalise` acontece **por consequência** de assumir, e não por um botão
separado: quem assume está começando a analisar, e um passo manual a mais só
criaria a janela de existir caso com responsável e status "ninguém pegou".

---

## Decisão 3 — Resolvido é imutável

Não há reabertura, não há nota depois da conclusão, não há troca de
responsável. Cinco tentativas são recusadas, e o Security Gate 7 verifica as
cinco.

A razão não é purismo: **a Fase 9 usa o resultado humano como verdade
apurada**. Um veredito que muda depois faria backtests antigos passarem a
mentir sem que ninguém percebesse. Se a operação precisar corrigir uma
conclusão, o caminho honesto é um caso novo — que a timeline registra.

---

## Decisão 4 — Duas camadas contra o lost update

O ROADMAP 7.5 pede concorrência otimista. Ela existe em dois lugares, e os dois
fazem falta:

| Camada | O que ela pega |
|---|---|
| `versao` enviada pelo cliente, conferida na aplicação | O caso comum: alguém agindo sobre uma tela velha. Devolve `409` sem tocar no banco |
| `versao` como token de concorrência do EF | A corrida real: duas requisições que passam **juntas** pela primeira checagem. O `UPDATE` sai com `WHERE versao = @lida` e a segunda atinge zero linhas |

Sem a segunda, dois analistas resolvendo o mesmo caso ao mesmo tempo gravariam
resultados diferentes e o último venceria em silêncio. O teste dispara as duas
resoluções em paralelo e exige exatamente um `200` e um `409`.

`versao` no corpo **não é mass assignment**: ela nunca é atribuída, só
comparada. É o cliente dizendo "eu estava vendo o estado N".

---

## Decisão 5 — Recusar texto, nunca limpar

Notas e títulos são recusados quando parecem carregar marcação, e **nunca
limpos em silêncio**. Uma nota que o sistema altera sozinho deixa de ser o que
o analista escreveu — e numa investigação isso é pior do que uma recusa com
explicação.

A regra é estreita de propósito: `<` seguido de letra ou de barra. Assim
`valor < 100` continua sendo uma nota válida, enquanto `<script>` e `</b>` são
recusados. Recusar todo `<` tornaria impossível escrever uma comparação
numérica — exatamente o que se escreve investigando fraude.

**A defesa de verdade contra XSS é a saída.** A tela renderiza texto; nunca
`dangerouslySetInnerHTML`. A validação de entrada é a segunda camada, e não a
primeira. Um teste prova que a nota volta do servidor byte a byte como foi
escrita — sem escape no meio do caminho, que faria a nota exibida diferir da
nota gravada.

**Notas não são editáveis nem apagáveis.** Uma correção relevante vira uma nota
nova, que a timeline registra na ordem em que aconteceu.

---

## Decisão 6 — Assumir é para si; transferir é supervisão

| Ação | Quem pode |
|---|---|
| Assumir | qualquer perfil operacional, e só sobre caso sem responsável |
| Transferir | Administrador e Supervisor |
| Resolver | o responsável, ou a supervisão |
| Anotar, associar alerta | qualquer perfil operacional |
| Qualquer coisa | **nunca** o Auditor |

Tirar o caso de outra pessoa é ato de supervisão (`CLAUDE.md` seção 8.2). Sem
a separação, qualquer analista poderia esvaziar a fila de outro sem que ninguém
tivesse decidido isso.

O destino de uma transferência é validado: precisa existir **no mesmo tenant**,
estar ativo e não ser Auditor — um Auditor responsável criaria um caso que
ninguém pode resolver.

---

## Decisão 7 — As ações permitidas vêm do servidor

O workspace recebe `acoesPermitidas` em cada leitura. A tela mostra o que o
servidor listou; o servidor recusa de novo quando a ação chega.

São as duas coisas, e não uma no lugar da outra. O `CLAUDE.md` seção 52 é
explícito: esconder botão não é autorização. Mas deixar a tela **deduzir** a
regra também é errado — ela ofereceria ações que o backend recusaria, ou
esconderia ações válidas, no dia em que a regra mudasse.

---

## Decisão 8 — Rotas de ação, e não um `PUT` no recurso

`assumir`, `transferir`, `resolucao`, `notas`, `alertas` são rotas próprias.
Não existe `PUT /casos/{id}`.

Um `PUT` aceitando o objeto inteiro convidaria ao mass assignment que o produto
recusa: bastaria mandar `status` e `resultado` no corpo. Com rotas de ação,
cada transição carrega sua própria regra, e o DTO de entrada não tem onde
esconder um campo interno. O JSON estrito completa: campo desconhecido é `400`,
e não silêncio.

---

## Decisão 9 — A timeline não é navegação do caso

`EventoDoCaso` e `NotaDoCaso` são entidades com FK para o caso, gravadas
explicitamente pelo repositório. O agregado acumula apenas o que **esta
operação** produziu.

Duas razões, e a segunda foi um defeito real desta fase:

1. **A timeline é append-only e nenhuma regra de domínio a lê.** O caso só
   acrescenta. Carregá-la para gravar uma nota traria centenas de linhas para
   não consultar nenhuma.

2. **O EF trata filho novo com chave preenchida como `UPDATE`.** Os
   identificadores são gerados pelo domínio (UUIDv7), então quando o EF
   encontrava uma entrada nova dentro de uma coleção de navegação ele concluía
   que a linha já existia e emitia `UPDATE` — que atingia zero linhas e virava
   um **falso conflito de concorrência**. Toda nota falhava com `409`.

---

## Decisão 10 — A timeline tem sequência, e não só horário

Cada entrada carrega uma `Sequencia` por caso, e é por ela que a trilha é
ordenada.

Horário não basta: abrir um caso grava dois eventos no mesmo instante, e o
identificador não desempata — o UUIDv7 sorteia os bits finais, então dois
gerados no mesmo milissegundo não saem em ordem. Sem a sequência, a timeline
mostraria "alerta associado" antes de "caso aberto" de vez em quando, e uma
história que muda de ordem entre duas leituras deixa de ser prova.

O índice `(caso, sequencia)` é único: duas entradas na mesma posição
significariam que a história se bifurcou.

---

## Decisão 11 — O veredito é por transação, e não por caso

Resolver um caso grava uma linha em `resultados_de_investigacao` para **cada
transação** dos seus alertas, com restrição única em `transacao_id`.

É este registro — e não o caso — que a Fase 9 vai comparar contra regras
candidatas. Um caso reúne várias transações; uma estatística de backtest
precisa da unidade que ela mede.

A restrição única existe porque uma transação com dois vereditos contaria duas
vezes numa estatística. O caminho já torna isso improvável — um alerta por
avaliação, uma avaliação por transação, um caso por alerta — mas improvável não
é impossível.

---

## Consequências

**Ganhos**

- O ciclo do produto fecha: transação → avaliação → alerta → caso → veredito.
- O falso positivo legítimo é demonstrável ponta a ponta (`Revisar` →
  `Legitima`), como o `CLAUDE.md` seção 94 exige.
- Lost update provado com duas requisições simultâneas, e não só descrito.
- A Fase 9 recebe uma base de verdade apurada, por transação.

**Custos aceitos**

- Sem reabertura de caso. Corrigir uma conclusão exige um caso novo.
- A validação de marcação é uma heurística: uma nota legítima que contenha
  `<b` será recusada. A alternativa — limpar em silêncio — foi julgada pior.
- O agregado carrega o contador de eventos, uma coluna a mais no caso.

**Dívida registrada**

- Sem transferência a partir da tela: a rota existe e é testada, mas o
  workspace só oferece assumir e resolver. Fase 13, no polish.
- Sem busca por texto em casos e notas. Fase 10.
- Sem paginação na tela de casos (a API pagina; a tela ainda não navega).
