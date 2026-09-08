# Custo e infraestrutura — a conta antes do deploy

Verificação feita em **2026-09-08**, contra as páginas oficiais de preço. O
`CLAUDE.md` seção 76 fixa a meta:

> **AWS com custo esperado de US$ 0,00 para o uso de portfólio.**

E a seção 22 proíbe repetir número de preço sem fonte. Todo valor abaixo tem
link para onde ele foi lido.

---

## 1. O envelope

O dimensionamento vem do `CLAUDE.md` seção 77 — referência, não SLA:

| Item | Volume mensal |
|---|---|
| Avaliações de risco | ~10.000 |
| Eventos operacionais | ~10.000 |
| Backtests | até ~100 |
| Banco | abaixo de 500 MB |
| Tráfego humano | um punhado de sessões de demonstração |

Dez mil avaliações por mês são **cerca de 14 por hora**. Isso importa mais do
que parece, e é o que a seção 4 explora: o sistema fica ocioso a maior parte do
tempo, e é a ociosidade — não o pico — que decide a conta.

---

## 2. Preços verificados

| Serviço | Camada gratuita | Fonte |
|---|---|---|
| **Lambda** | 1.000.000 requisições e 400.000 GB-s por mês, **sem expirar** | [Lambda pricing](https://aws.amazon.com/lambda/pricing/) |
| **SQS** | 1.000.000 requisições por mês | [SQS pricing](https://aws.amazon.com/sqs/pricing/) |
| **CloudWatch Logs** | 5 GB por mês, somando ingestão, arquivo e consulta | [CloudWatch pricing](https://aws.amazon.com/cloudwatch/pricing/) |
| **EventBridge Scheduler** | 14.000.000 invocações por mês | [EventBridge pricing](https://aws.amazon.com/eventbridge/pricing/) |
| **SSM Parameter Store** | parâmetros **padrão** e suas chamadas de API: sem custo | [Systems Manager pricing](https://aws.amazon.com/systems-manager/pricing/) |
| **Neon** | 0,5 GB por projeto, **100 CU-horas por mês**, suspende após 5 min | [Neon pricing](https://neon.com/pricing) |
| **Vercel Hobby** | gratuito, 1.000.000 invocações de função, 100 deploys/dia | [Vercel Hobby](https://vercel.com/docs/plans/hobby) |

Acima da faixa gratuita, os preços que importam: **CloudWatch Logs a US$ 0,50
por GB** ingerido e **US$ 0,03 por GB-mês** de armazenamento; **EventBridge
Scheduler a US$ 1,00 por milhão** de invocações; **parâmetro avançado do SSM a
US$ 0,05 por mês**, que é o motivo de este projeto usar apenas parâmetros
padrão.

---

## 3. A estimativa

### Lambda

| Função | Invocações/mês | Observação |
|---|---|---|
| API (Function URL) | ~50.000 | ingestão mais a navegação humana da demonstração |
| Consumidor de eventos | ~1.000 | 10.000 mensagens em lotes de até 10 |
| Consumidor de backtests | ~100 | um por execução |
| Despachante da Outbox | ~2.900 | ver seção 4 — a cada 15 minutos, mais as chamadas sob demanda |
| **Total** | **~54.000** | **5,4% da camada gratuita** |

Duração: a API em 512 MB, com p50 medido de 19 ms e cauda de cold start de
alguns segundos, fica em torno de 0,1 GB-s por invocação. Cinquenta e quatro mil
invocações dão **~6.000 GB-s**, ou **1,5% dos 400.000** gratuitos.

**A folga é grande o bastante para absorver o cold start do .NET**, que é o
maior risco de duração desta pilha e o assunto da seção 6.

### SQS

Cada evento custa três requisições — enviar, receber, apagar. Dez mil eventos
dão 30.000. O gatilho de SQS no Lambda também consulta a fila continuamente,
mas **esse polling roda na infraestrutura da AWS**, e com consulta longa de 20
segundos ele soma cerca de 130.000 requisições por mês.

**Total: ~160.000, ou 16% do gratuito.** É o serviço com menos folga
proporcional, e ainda assim sobra mais de 80%.

### CloudWatch Logs

Uma linha de log por requisição (Fase 12), mais as linhas do caminho
assíncrono: aproximadamente quatro linhas por operação, a ~500 bytes cada em
JSON.

```
10.000 operações × 4 linhas × 500 B ≈ 20 MB/mês
```

**0,4% dos 5 GB.** A retenção fica em 14 dias mesmo assim — log guardado é
custo que só aparece no terceiro mês.

### EventBridge Scheduler, SSM e Vercel

Ordens de grandeza abaixo do gratuito: 2.900 invocações contra 14 milhões, um
punhado de parâmetros padrão, e um site estático com trânsito de demonstração.

Sobre o SSM, um detalhe que só apareceu na implementação: cada arranque frio de
função faz **uma** chamada `GetParametersByPath`, e não uma por parâmetro — o
provedor de configuração lê o caminho inteiro de uma vez. Com quatro funções e
arranque frio esporádico, a ordem de grandeza é de centenas de chamadas por
mês. Parâmetros **padrão** não cobram por chamada de API; os avançados cobram
US$ 0,05 por parâmetro por mês, e é por isso que o projeto usa os padrões.

### Total esperado

> **US$ 0,00 por mês**, com o item mais apertado em 16% da sua cota.

---

## 4. O achado que decide a arquitetura de deploy

**O plano gratuito do Neon dá 100 CU-horas por mês e suspende o compute após 5
minutos de inatividade.** Um mês tem 730 horas. Mesmo na menor unidade de
autoscaling, um banco que nunca suspende consome:

```
730 h × 0,25 CU = 182 CU-horas   →   quase o dobro do gratuito
```

Ou seja: **manter o banco acordado é o único jeito de esta arquitetura custar
dinheiro.** E o desenho atual faz exatamente isso — o despachante da Outbox
consulta o banco a cada 2 segundos.

O que cada frequência de varredura implica, sabendo que o Neon só suspende
depois de 5 minutos parado:

| Varredura | Banco acordado | CU-horas/mês (a 0,25 CU) | Cabe? |
|---|---|---|---|
| a cada 2 s (hoje, em processo) | 100% | ~182 | **não** |
| a cada 5 min | ~100% — nunca chega a suspender | ~182 | **não** |
| a cada 15 min | ~33% | ~61 | sim |
| a cada 1 h | ~8% | ~15 | sim, com folga |

### A decisão

**Publicação imediata, varredura esparsa.**

1. Terminada a transação crítica, a própria API invoca o Lambda do despachante
   de forma assíncrona. O evento sai em segundos, e o banco já estava acordado
   — a requisição acabou de usá-lo.
2. Um agendamento a cada **15 minutos** varre a Outbox como rede de segurança,
   para o caso de a invocação ter falhado.

Isso não muda o código de dentro do ciclo, que é o que o
[ADR 0010](adr/0010-backbone-assincrono.md) já previa para esta fase. Muda
**quem aciona** o ciclo.

**Por que a Outbox continua necessária mesmo com publicação imediata.** A
invocação assíncrona pode falhar, e falha depois do commit. Sem a Outbox, o
evento se perderia em silêncio; com ela, a varredura o encontra no ciclo
seguinte. A publicação imediata é otimização de latência — a garantia continua
sendo a tabela.

### O outro lado do mesmo achado

O primeiro acesso depois de horas de silêncio vai esperar o Neon acordar. Numa
demonstração de portfólio esse é **o caso comum**, não a exceção: ninguém abre o
sistema por dias, e então um recrutador clica no link.

Isso é assunto da seção 6, junto do cold start do Lambda — os dois se somam na
mesma requisição.

---

## 5. Onde o dinheiro apareceria

Os cinco caminhos que quebrariam o zero, e o que os contém:

| Risco | Contenção |
|---|---|
| Banco acordado 24/7 | varredura a cada 15 min, e não a cada 2 s (seção 4) |
| Log crescendo sem teto | retenção de 14 dias; payload nunca é registrado (Fase 12) |
| Laço de retry infinito | limite de tentativas e fila de mortas desde a Fase 5 |
| Backtest pesado | teto de transações analisadas por execução (Fase 9) |
| Recurso com custo fixo | nenhum na pilha: sem NAT Gateway, sem RDS, sem ElastiCache, sem API Gateway |

A última linha é a mais importante e a mais fácil de perder de vista: **tudo
nesta arquitetura cobra por uso.** Um único NAT Gateway custaria cerca de US$ 32
por mês parado, sem tráfego nenhum — mais do que todo o resto somado, e para
sempre. É por isso que o `CLAUDE.md` seção 75 o proíbe por padrão.

---

## 6. O que ainda não está medido

Nomeado para não ser confundido com cobertura:

- **Cold start do .NET em Lambda não foi medido.** É a maior incerteza desta
  fase. A estimativa de GB-s tem folga de duas ordens de grandeza, então ele
  não ameaça o custo — ameaça a experiência da primeira requisição, somado ao
  despertar do Neon.
- **A soma dos dois despertares** — Lambda frio mais Neon suspenso — não tem
  número. Só medindo depois do deploy.
- **O polling do gatilho de SQS** foi estimado a partir do comportamento
  documentado de consulta longa, e não medido.
- **Nenhum recurso foi criado.** Todos os números acima são projeção sobre
  preço publicado, e não fatura.

---

## 7. Guardrails a configurar no dia do deploy

Nenhum deles foi criado — exigem autorização (`CLAUDE.md` seção 109):

- **AWS Budget** de US$ 1,00 com alerta por e-mail em 80%. Um dólar é
  deliberadamente baixo: a meta é zero, então qualquer centavo é anomalia.
- **Retenção de log** de 14 dias em todos os grupos.
- **Concorrência reservada** de 5 na API e 2 em cada worker. Não é para
  economizar — é para que um laço acidental não consuma a cota mensal numa
  tarde.
- **Alarme de fila de mortas** com limiar 1: qualquer mensagem morta merece
  aviso.

---

## 8. O que precisa existir antes do primeiro deploy

Nada disto foi criado — todos exigem autorização (`CLAUDE.md` seção 109). A
lista existe porque o template **não** cria nenhum deles de propósito.

### 8.1 Os parâmetros no SSM

O template não declara parâmetro nenhum, e a ausência é a decisão: o que
precisa estar no Parameter Store é segredo, e segredo dentro de um arquivo de
infraestrutura versionado é um segredo publicado (`CLAUDE.md` seção 59).

As funções leem o caminho `/portfolio/central-antifraude/{ambiente}/` no arranque, e cada
parâmetro vira chave de configuração trocando `/` por `:`:

| Parâmetro | Tipo | Vira a chave |
|---|---|---|
| `/portfolio/central-antifraude/producao/ConnectionStrings/Postgres` | `SecureString` | `ConnectionStrings:Postgres` |
| `/portfolio/central-antifraude/producao/Autenticacao/ChaveDeAssinatura` | `SecureString` | `Autenticacao:ChaveDeAssinatura` |
| `/portfolio/central-antifraude/producao/Ingestao/ChaveDeFingerprint` | `SecureString` | `Ingestao:ChaveDeFingerprint` |

Os dois últimos não são conveniência: a chave de assinatura é o que separa uma
sessão legítima de uma forjada, e a de fingerprint é o segredo do HMAC que
substitui o IP bruto (`CLAUDE.md` seção 57). Um valor previsível em qualquer um
dos dois derruba a garantia que a fase correspondente construiu.

O provedor está configurado como **obrigatório**: faltando o caminho, a função
não sobe. É deliberado — uma função no ar que falha em toda avaliação de risco
é pior do que uma que não subiu, porque o alarme de saúde não dispara.

### 8.2 O que NÃO vai para o SSM

A origem do frontend chega por variável de ambiente, declarada no template.
Guardá-la também no Parameter Store criaria um segundo lugar para a mesma
verdade — e é o segundo lugar que sai de sincronia no primeiro ajuste.

### 8.3 O banco

O Neon precisa existir antes da primeira invocação, com as migrations
aplicadas. A string de conexão deve usar o endpoint **com pooling**: uma função
Lambda por invocação abriria conexões diretas mais rápido do que o Postgres as
libera.

---

## 9. O que foi implantado, e o que a nuvem ensinou

**Implantado em 2026-09-08.** Conta AWS `632404567709`, região `us-east-1`,
stack `central-antifraude-producao`.

### 9.1 Inventário real

26 recursos, todos previstos — nenhum apareceu por engano:

| Tipo | Qtd. | Cobra? |
|---|---|---|
| `AWS::Lambda::Function` | 4 | por uso |
| `AWS::Lambda::Url` | 1 | não |
| `AWS::Lambda::EventSourceMapping` | 2 | não |
| `AWS::SQS::Queue` | 4 (2 de trabalho + 2 DLQ) | por uso |
| `AWS::Scheduler::Schedule` | 1 | por uso |
| `AWS::Logs::LogGroup` | 4 | por volume |
| `AWS::CloudWatch::Alarm` | 3 | 10 grátis, always-free |
| `AWS::IAM::Role` | 5 | não |
| `AWS::Lambda::Permission` | 2 | não |

Nenhum RDS, EC2, ECS, NAT Gateway ou API Gateway. **Nenhum recurso de custo
fixo.**

Configuração conferida no ambiente real: as 4 funções em `dotnet10`/`arm64`,
retenção de 14 dias nos 4 grupos de log, scheduler em `rate(15 minutes)` e
`ENABLED`, redrive nas duas filas de trabalho (`maxReceiveCount` 5 e 3).

Parâmetros no SSM: 3, todos `SecureString` com `alias/aws/ssm` — a chave
gerenciada pela AWS, que **não cobra**. Uma chave própria (CMK) custaria US$ 1,00
por mês, e teria sido o único custo fixo do projeto.

### 9.2 Custo observado

Gasto da conta no mês, por serviço, depois do deploy:

```
AWS Key Management Service   0,1042   <- IAM Identity Center, alheio a este projeto
AWS Secrets Manager          0,00001  <- chamadas de API, nenhum segredo armazenado
Amazon S3                    0,000055 <- artefatos de deploy do SAM
```

**A Central Antifraude não acrescentou nenhuma linha de custo.** A cobrança de
KMS é uma chave replicada do IAM Identity Center, em `PendingReplicaDeletion` —
some sozinha e não pertence a esta stack.

Proteção: budget `central-antifraude-meta-zero` de **US$ 1,00/mês**, com alertas
por e-mail em 50%, 80% e 100% do realizado e 100% do previsto. Budgets sem
*ações* são gratuitos e ilimitados; só os "action-enabled" têm limite de dois.

### 9.3 O que a nuvem ensinou, e que a validação local não pegaria

Cinco defeitos passaram por template válido, lint limpo, 1.073 testes verdes e
uma stack `CREATE_COMPLETE`. Todos falhavam em silêncio.

| Defeito | Como se manifestava | Teste que hoje o pega |
|---|---|---|
| Reserva de concorrência acima do limite da conta | rollback da stack inteira | `Nenhuma_funcao_reserva_concorrencia` |
| IAM sem o ARN do *caminho* do SSM | 502 em toda invocação | `Cada_funcao_le_o_caminho_do_SSM_e_os_parametros_nele` |
| CORS declarado na Function URL **e** na aplicação | `curl` 200, navegador reprova | `O_CORS_e_declarado_em_um_lugar_so` |
| `IContextoDoUsuarioAtual` ausente no contêiner dos workers | despachante morria em toda invocação | `HospedagemDosLambdasTests` (5 testes) |
| Backtest não acordava o despachante | ficava "Pendente" por até 15 min | `Solicitar_backtest_tambem_acorda_o_despachante` |

O padrão dos cinco é o mesmo: **a fronteira entre o que a aplicação declara e o
que a nuvem exige**. Nenhum é bug de lógica de domínio, e nenhum apareceria sem
criar recurso.

O mais instrutivo é o da concorrência. A conta tem limite de **10** execuções
simultâneas — e não os 1.000 do padrão, porque contas novas começam baixo e
sobem com o uso. As quatro reservas somavam exatamente 10, e a AWS exige deixar
10 não reservados. O efeito de removê-las é melhor do que o plano original: o
limite da conta já é um teto, e é global.

### 9.4 O que continua não medido

- Cold start em condição realmente fria de longo prazo (dias sem acesso).
- Comportamento sob carga: o envelope de portfólio nunca foi exercido de perto.
- Custo ao longo de um mês inteiro — o deploy tem horas de vida.

