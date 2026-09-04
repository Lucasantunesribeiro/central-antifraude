# ADR 0008 — Motor de risco determinístico

- **Status:** aceito
- **Data:** 2026-09-04
- **Fase:** 3 — Motor de Risco Determinístico v1

## Contexto

O `CLAUDE.md` (seção 19) fixa que o motor principal de risco é
**determinístico**: a mesma entrada, o mesmo contexto histórico relevante e a
mesma versão de regras produzem o mesmo resultado. IA não participa do cálculo
do score (seções 19 e 64).

A seção 17 exige que cada avaliação registre informação suficiente para
responder, anos depois:

> Por que esta decisão foi tomada naquele momento?

E a seção 21 proíbe DSL própria, SQL configurável e script arbitrário de
usuário: o sistema deve ter um **catálogo fechado e tipado** de tipos de regra.

Estas três exigências, juntas, decidem quase toda a arquitetura desta fase.

---

## Decisão 1 — O motor é uma função pura

`MotorDeRisco.Avaliar` recebe `(transação, versão do perfil, contexto,
instante)` e devolve uma `AvaliacaoDeRisco`. Não há dentro dele banco, relógio,
rede, aleatoriedade nem IA.

O instante chega por parâmetro e serve apenas para carimbar o resultado —
**nenhuma regra o usa para decidir**. As janelas de tempo são ancoradas em
`OccurredAt` da transação (ver Decisão 4).

Três consequências práticas:

| Consequência | Por quê |
|---|---|
| O motor inteiro roda sem infraestrutura | Todo teste de regra é unitário e rápido |
| O backtest da Fase 9 reusa o mesmo motor | Basta montar o contexto de outra fonte, sem reimplementar regra nenhuma (`CLAUDE.md` seção 25) |
| Determinismo é verificável | `MotorDeRiscoTests.Mesma_entrada_produz_o_mesmo_resultado` compara score, decisão, ordem, versões **e o texto de cada explicação** |

### Ordem estável

As regras são percorridas ordenadas por `TipoDeRegra`, e não na ordem em que
aparecem no perfil. Sem isso, duas instalações com a mesma configuração
devolveriam sinais em ordens diferentes, e comparar duas avaliações deixaria de
ser confiável.

### Falha alta diante de regra desconhecida

Um perfil que referencia um tipo de regra que este motor não sabe executar
**lança**, em vez de ignorar a regra. Ignorar em silêncio produziria uma
avaliação com score menor do que o perfil pede, e a explicação não mencionaria
a ausência — o pior dos dois mundos.

---

## Decisão 2 — Contexto carregado de uma vez, e não consultas dentro da regra

Cada avaliação carrega **um** `ContextoDeRisco`: o histórico do cliente,
projetado nos campos que as regras usam.

| Alternativa | Por que foi descartada |
|---|---|
| Cada regra consulta o banco | Duas regras poderiam discordar sobre o histórico dentro da mesma avaliação; o motor deixaria de ser testável sem banco; o backtest teria que simular um repositório |
| Carregar entidades `Transacao` completas | Traria colunas que nenhuma regra lê e amarraria o motor ao formato de persistência |

O contexto é limitado em **janela** (90 dias) e em **quantidade** (200
transações). Sem os dois limites, um cliente com anos de histórico faria o
custo da avaliação crescer sem teto dentro do caminho crítico da ingestão.

Há uma validação de inicialização que recusa subir se a janela de histórico for
menor que a maior janela que uma regra de velocidade pode declarar (24 h). Sem
ela, a regra perguntaria por um passado que o contexto não carregou e ficaria
**calada** — sem erro, sem aviso, e com score menor do que o perfil pede.

---

## Decisão 3 — Catálogo fechado, configuração é dado

Quatro tipos de regra, cada um com:

- um `record` de configuração tipado, com validação própria;
- um evaluator C# dedicado;
- uma entrada explícita no `switch` de serialização.

O que fica no banco é um `jsonb` com **números e o nome de um tipo do enum**.
Não há expressão, SQL nem script. Na leitura, o `switch` fechado escolhe o
`record` a reconstruir; um tipo desconhecido **falha alto**.

`SecurityGate3Tests.Regra_adulterada_no_banco_nao_vira_comportamento_novo`
simula o pior caso — alguém com acesso de escrita ao banco troca o tipo por um
valor inventado — e verifica que a leitura estoura em vez de executar algo que
ninguém revisou.

### Por que quatro regras, e não uma dúzia

Cada uma reconhece um padrão diferente e produz um caso demonstrável. Regras
que só repetiriam o que outra já diz aumentariam o número no README sem
aumentar o que o produto sabe.

---

## Decisão 4 — Janelas ancoradas em `OccurredAt`, nunca em "agora"

A regra de velocidade mede a transação contra os **vizinhos dela no tempo em
que aconteceu**, e não contra o relógio do servidor.

Uma rajada de card testing que chega com dois dias de atraso continua sendo uma
rajada. Ancorar em "agora" faria o grupo inteiro cair fora da janela, e a
fraude passaria despercebida apenas por causa do atraso — o oposto do que o
`CLAUDE.md` seção 16 pede para eventos atrasados.

O intervalo é **semiaberto**: `(referência − janela, referência]`. Exatamente
10 minutos antes fica fora. A escolha oposta seria igualmente defensável; o que
não pode é ficar indefinida, e por isso ela tem teste próprio.

A regra também conta **a transação avaliada** junto das anteriores. Sem isso,
"máximo 3" significaria na prática "3 anteriores mais esta" — e o número
configurado mentiria para quem o configurou.

---

## Decisão 5 — Ausência de dado nunca vira sinal de risco

Fingerprint de dispositivo ausente, país ausente, histórico sem país, histórico
mais curto que o mínimo: em todos esses casos a regra **se cala**.

Tratar "não sei" como "suspeito" faria toda integração que não envia um campo
opcional gerar risco em toda transação — e encheria a fila do analista de
alertas que não dizem nada.

Cada regra tem teste explícito para isso.

---

## Decisão 6 — Explicabilidade gravada, não recalculada

Cada avaliação persiste:

- score, decisão e instante;
- a **versão do perfil** usada, com o número dela;
- a versão do motor;
- um `SinalDeRisco` por regra acionada, com pontos, texto da explicação, os
  dados numéricos que a sustentam (`jsonb`) e a **versão exata da regra**.

O texto da explicação é montado deterministicamente pelo evaluator — nunca por
IA. É por isso que ele pode ser gravado junto do sinal e continuar verdadeiro
anos depois.

Duas chaves estrangeiras com `RESTRICT` transformam a explicabilidade histórica
em restrição de banco:

- `avaliacoes_de_risco.versao_de_perfil_id → versoes_de_perfil_de_risco`
- `sinais_de_risco.versao_de_regra_id → versoes_de_regra`

Enquanto existir uma avaliação apontando para uma versão, o banco recusa
apagá-la. Sem isso, uma limpeza futura deixaria avaliações órfãs e a pergunta
"por que esta decisão foi tomada" ficaria sem resposta.

### Score é limitado, e o teto fica visível

O score final é limitado a 0..100, mas `SomaBrutaDosPontos` e `ScoreFoiLimitado`
continuam registrados e vão para a tela. Sem isso, um analista somaria as
contribuições exibidas, chegaria a 130 e concluiria que o sistema errou uma
conta simples.

---

## Decisão 7 — A avaliação entra na mesma gravação da transação

`ServicoDeIngestao` avalia **antes** de `SaveChanges`, e as duas linhas entram
no mesmo commit. Uma transação registrada sem avaliação seria um estado que
nenhuma tela sabe mostrar e que nenhuma regra sabe corrigir depois.

No **retry**, a avaliação é **lida do banco**, nunca recalculada. Recalcular
faria o mesmo pedido receber decisões diferentes conforme o histórico crescesse
entre a primeira tentativa e a repetição — e o integrador não teria como saber
qual das duas vale.

Uma restrição única (`ix_avaliacoes_de_risco_transacao`) garante uma avaliação
por transação **no banco**, e não apenas no código: duas avaliações da mesma
transação seriam duas decisões concorrentes, e nenhuma tela saberia qual
mostrar.

---

## Decisão 8 — O catálogo padrão nasce junto da organização

Uma organização sem perfil ativo não conseguiria avaliar transação nenhuma, e
descobrir isso na primeira ingestão — em produção, com o integrador esperando
resposta — seria tarde.

`ProvisionamentoDeRisco.GarantirCatalogoAsync` é idempotente e roda na criação
da organização (seed e cenário de teste). A gestão pelo Supervisor é a Fase 8;
até lá **não há rota HTTP que altere regra ou perfil**, e há teste que verifica
a ausência dessas rotas.

---

## As regras e suas fontes

As **práticas** são reais e têm fonte. Os **números** são configuração de
demonstração deste projeto.

Fonte única desta fase: U.S. Payments Forum, *"Card-Not-Present (CNP) Fraud
Mitigation Techniques"*, julho de 2020 — consultado em 2026-09-04.

| Regra | Citação | Peso (demo) | Configuração (demo) |
|---|---|---|---|
| Velocidade por cliente | Seção 8: *"Velocity checks monitor the number of times that certain transaction data elements occur within certain intervals…"*; *"A velocity check is made up of three or more variables, always including quantity, data element, and timeframe"* | 35 | mais de 3 em 10 min |
| Dispositivo novo | Seção 10: *"Browser identifiers are a subset of more general device identifiers…"*; *"If a device is unknown, the enterprise could use additional step-up authentication"* | 20 | mínimo 3 no histórico |
| Valor acima do histórico | Seção 13, gatilhos de alerta ao portador: *"Transaction amount > baseline spending"* | 30 | 5× a média, mínimo 3 |
| Divergência geográfica | Seção 13: *"Geolocation — New place, first transaction with large amount"* | 25 | mínimo 3 no histórico |

O documento traz um exemplo ilustrativo de velocidade — *"five transactions in
15 minutes"* — e **não** um número prescrito. Os valores acima são escolhas
deste projeto.

### Os limiares

| Faixa | Decisão |
|---|---|
| 0–39 | Permitir |
| 40–69 | Revisar |
| 70–100 | Bloquear |

Escolhidos para que **uma regra sozinha nunca bloqueie**: a mais pesada vale 35
pontos, e bloquear exige ao menos duas evidências independentes. Um único sinal
levando direto ao bloqueio geraria muito falso positivo e tornaria a
investigação humana — o propósito do produto — decorativa. Há teste que trava
essa propriedade.

O limiar pertence à faixa mais severa: 40 já é Revisar, 70 já é Bloquear.

**Nada disso é padrão de mercado nem recomendação oficial** (`CLAUDE.md`
seções 22 e 112). A tela de regras diz isso ao usuário, com essas palavras.

---

## Decisão 9 — País declarado pela integração, não GeoIP

A divergência geográfica usa o país que a origem envia. A Central Antifraude
não guarda o endereço IP (ADR 0007), só o HMAC dele — então não há como derivar
localização aqui, e introduzir uma base GeoIP resolveria um problema que o
produto não tem.

---

## Consequências

**Ganhos**

- Decisão explicável, com procedência gravada e verificável anos depois.
- Motor testável sem infraestrutura; 4 tipos de regra com cobertura de limite
  exato, um passo abaixo, um passo acima e dado ausente.
- Base pronta para o backtest da Fase 9 sem duplicar lógica de regra.
- Configuração de regra que não pode virar execução arbitrária.

**Custos aceitos**

- O contexto é uma consulta a mais no caminho crítico da ingestão. Medida e
  limitada; a Fase 4 volta ao assunto sob concorrência.
- Regras novas exigem código novo — evaluator, configuração e entrada no
  serializador. É exatamente a barreira que a seção 21 pede.
- O catálogo é fixo até a Fase 8. Trocar peso hoje exige nova versão de regra,
  o que só existirá com a gestão publicada.

**Dívida registrada**

- Não há índice específico para o par (cliente, janela) além do já criado em
  `(organizacao, cliente_externo_id, ocorrida_em)`. A Fase 4 mede o plano real
  antes de acrescentar qualquer outro (`CLAUDE.md` seção 46).

---

## Defeito encontrado e corrigido nesta fase

**Horário mudava ao passar pelo banco.** A resposta da ingestão carrega
`avaliadaEm`. Na primeira requisição o valor vinha da memória; no retry, do
banco. O `DateTimeOffset` do .NET conta em ticks de 100 ns e o `timestamptz` do
PostgreSQL guarda microssegundos — o mesmo pedido devolvia dois horários que
diferiam em nanossegundos, e o integrador não teria como saber qual era o
verdadeiro.

Corrigido em `Instante.Normalizar`, aplicado no relógio do sistema e nos tempos
da transação: o que entra na memória é exatamente o que o banco vai devolver.
Bug real virou proteção permanente (`CLAUDE.md` seção 89).
