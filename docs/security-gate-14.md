# Security Gate 14 — Infraestrutura, custo e deploy

**Data:** 2026-09-08
**Escopo:** o que a Fase 14 acrescentou à superfície de ataque — template do
CloudFormation, funções Lambda, filas SQS, Function URL, Parameter Store e o
frontend hospedado.

**Executado em duas rodadas.** A primeira sobre a infraestrutura declarada,
antes de existir recurso; a segunda contra o ambiente publicado, em
2026-09-08. A seção 11 traz o resultado da segunda — e é ela que vale como
prova, porque três das conclusões da primeira estavam erradas.

---

## 1. Como este gate é executado

Diferente dos gates anteriores, este é **automatizado e roda sem banco**:

```
tests/CentralAntifraude.ArchitectureTests/SecurityGate14Tests.cs
```

Os gates de 1 a 12 exercitam a aplicação no ar e precisam de PostgreSQL. Este
verifica propriedades de um arquivo, e um gate que exige subir um contêiner
para ler um YAML é um gate que alguém pula quando está com pressa.

São 14 asserções, e todas passam.

---

## 2. Segredos

| Verificação | Resultado |
|---|---|
| Nenhuma palavra de segredo nos arquivos de `infra/` | ✅ automatizado |
| String de conexão fora de variável de ambiente | ✅ automatizado |
| Template não declara `AWS::SSM::Parameter` | ✅ automatizado |
| Leitura do SSM limitada a `central-antifraude/${Ambiente}/*` | ✅ automatizado |

**A decisão de fundo.** Variável de ambiente do Lambda é a opção mais fácil e a
mais traiçoeira para um segredo: parece segura e aparece em texto puro para
qualquer pessoa que abra a configuração da função no console. Os segredos ficam
no Parameter Store como `SecureString`, criados **fora** do template — porque um
`AWS::SSM::Parameter` no template teria que carregar o valor junto, e o valor é
o segredo (`CLAUDE.md` seção 59).

O provedor de configuração é `ConfiguracaoDaNuvem`, e ele só age quando a
variável `AWS_LAMBDA_FUNCTION_NAME` existe — ou seja, dentro de um Lambda de
verdade. O critério **não** é o nome do ambiente: a suíte de integração roda
como `Production` de propósito, e passaria a tentar falar com a AWS sem
credencial em toda execução.

Quais parâmetros precisam existir está em
[`custo-e-infraestrutura.md` seção 8.1](custo-e-infraestrutura.md).

---

## 3. IAM

| Verificação | Resultado |
|---|---|
| Nenhuma política crua (`PolicyDocument`) no template | ✅ automatizado |
| Nenhuma ação ou recurso com curinga | ✅ automatizado |
| Nenhum `AdministratorAccess` | ✅ automatizado |
| Invocação de Lambda restrita a **uma** função | ✅ inspeção |

Toda permissão vem de política nomeada do SAM, com o recurso preso ao ARN
correspondente:

- `SQSSendMessagePolicy` por fila, nomeada;
- `SSMParameterReadPolicy` presa ao caminho do ambiente;
- `LambdaInvokePolicy` presa **ao despachante**, e não a `lambda:InvokeFunction`
  em tudo. É o que transforma um erro de código em erro de permissão, em vez de
  numa invocação inesperada.

Receber e apagar mensagem não aparecem em política nenhuma: vêm do gatilho de
SQS, que o SAM configura sozinho na role da função que consome aquela fila.

**Só verificável na nuvem:** se as roles que o SAM gera a partir dessas
políticas são suficientes na prática. O teste de permissão é o deploy.

---

## 4. Exposição pública

| Verificação | Resultado |
|---|---|
| Uma única Function URL, na API | ✅ automatizado |
| Um único `AuthType: NONE` | ✅ automatizado |
| CORS preso à origem do frontend | ✅ automatizado |
| Nenhum recurso em VPC pública | ✅ não há VPC |

`AuthType: NONE` na API é deliberado, e não uma folga: a autenticação é da
aplicação e foi construída nas fases 1 e 11 — sessão com rotação de refresh,
RBAC no backend, verificação de `Origin`. Usar autenticação IAM na Function URL
exigiria que o navegador assinasse requisições com credenciais AWS, o que não
existe num frontend.

O mesmo `AuthType: NONE` numa função de worker seria um endpoint aberto para
invocar processamento de fila sem credencial. Por isso a contagem é asserção, e
não observação.

**O CORS aparece em dois lugares e precisa concordar:** o da Function URL e o da
aplicação (Fase 11). Ambos leem o mesmo parâmetro `OrigemDoFrontend`, porque
duas listas sairiam de sincronia no primeiro ajuste.

O lint do SAM apontou um erro real aqui: `OPTIONS` não é valor aceito em
`AllowMethods`. A Function URL responde ao preflight sozinha, e declarar o
método seria pedir permissão para algo que nunca chega à aplicação.

---

## 5. Dados e retenção

| Verificação | Resultado |
|---|---|
| Todo grupo de log com retenção declarada | ✅ automatizado |
| Nenhum dado sensível novo persistido | ✅ inspeção |

O padrão do CloudWatch é **nunca expirar**. Além da despesa, log guardado para
sempre é dado pessoal guardado para sempre — e o produto registra fingerprint de
dispositivo e o derivado HMAC do IP (`CLAUDE.md` seção 57). Os quatro grupos
declaram retenção explícita.

A Fase 14 não acrescentou campo persistido nenhum. O que ela acrescentou ao log
foi um evento novo (EventId 550, falha ao acordar o despachante), que registra a
exceção e nenhum dado de transação.

---

## 6. Resiliência e contenção de acidente

| Verificação | Resultado |
|---|---|
| Duas filas de trabalho com redrive para DLQ | ✅ automatizado |
| Toda função com teto de concorrência | ✅ automatizado |
| Falha ao acordar o despachante não derruba a resposta | ✅ teste unitário |
| Mensagem que falha é reportada, e não apagada em silêncio | ✅ teste unitário |

O teto de concorrência não é economia — é contenção. Sem ele, um laço de retry
mal resolvido consome a cota mensal numa tarde.

O item mais delicado desta fase é o penúltimo da tabela. No SQS com gatilho de
Lambda, **a AWS apaga tudo o que a invocação não reportar como falha**. O
consumidor confirma o sucesso, devolve o que recusa, mas **não devolve o que
falha por erro transitório** — ali ele deixa a visibilidade expirar, que é o
certo quando a fila é uma tabela. Contar apenas as devoluções deixaria essas
mensagens sem reporte, e a AWS as apagaria como se tivessem dado certo.

Por isso `FilaDoEventoLambda` conta por **subtração**: entregue e não confirmado
significa "não terminou bem", qualquer que tenha sido o motivo. Sete testes
prendem esse comportamento.

---

## 7. A ponte que o compilador não confere

O template aponta para código por **string**:

```
CentralAntifraude.Lambdas::CentralAntifraude.Lambdas.ConsumidorHandler::TratarAsync
```

Renomear a classe, mover o namespace ou trocar o nome do método deixa o build
verde, os testes verdes e a função morta — e o defeito só aparece na primeira
invocação depois do deploy, como erro de classe não encontrada no CloudWatch,
longe de quem fez a mudança.

`HandlersDoTemplateTests` fecha essa ponta: cada handler declarado é procurado
por reflexão no assembly real, com o método público de instância e o construtor
sem parâmetros que a AWS exige. Todo `CodeUri` também é conferido contra o disco.

---

## 8. Frontend (Vercel)

Cabeçalhos declarados em `frontend/vercel.json`:

- `Content-Security-Policy`
- `Strict-Transport-Security`
- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: DENY`
- `Referrer-Policy: no-referrer`

**Só verificável no deploy:** que a Vercel aplique os cabeçalhos como
declarados, e que a CSP não quebre nenhum recurso — as fontes são
auto-hospedadas desde a rodada de design, o que remove a única origem externa
que existia.

---

## 9. Segunda rodada: o gate contra o ambiente publicado

Conta `‹conta-aws›`, região `us-east-1`, stack `central-antifraude-producao`.

### 9.1 Segredos nos logs de produção

Varredura dos quatro grupos de log, duas horas de janela, procurando prefixo de
senha do Neon, `Password=`, nomes das chaves de assinatura e de fingerprint,
formato de chave de integração, blocos de chave privada e `AKIA`:

| Grupo | Eventos | Resultado |
|---|---|---|
| api | 299 | limpo |
| despachante | 35 | limpo |
| consumidor | 33 | limpo |
| backtests | 10 | limpo |

Nenhum evento casou com nenhum padrão. O log estruturado da Fase 12 registra
`CorrelationId`, `EventId` e `Decisao` — e não payload.

### 9.2 Autorização e isolamento, na API publicada

| Verificação | Esperado | Obtido |
|---|---|---|
| `GET /api/alertas` sem token | 401 | **401** |
| Auditor fazendo `POST /api/integracoes` | 403 | **403** |
| Auditor fazendo `GET /api/alertas` | 200 | **200** |
| `GET /api/casos/{guid de outro tenant}` | 404 | **404** |
| Token adulterado (um caractere a mais) | 401 | **401** |
| `POST /api/ingestao/transacoes` sem credencial | 401 | **401** |

O 404 no recurso alheio é a política da seção 52 do `CLAUDE.md`: cross-tenant não
revela existência.

### 9.3 Cabeçalhos e rate limiting

`referrer-policy: no-referrer`, `x-frame-options: DENY`,
`x-content-type-options: nosniff`. Não há `Server` nem `X-Powered-By` — a
Function URL não anuncia a pilha.

Rate limiting no login, medido: nove tentativas com senha errada devolveram 401,
e a décima devolveu **429**.

**HSTS não é emitido pela API**, e isso é correto: a Function URL só atende
HTTPS, e HSTS é instrução para o navegador sobre um *site*. Quem serve HTML é a
Vercel, e é lá que ele está declarado.

### 9.4 Sessão, rotação e CSRF, na aplicação publicada

O ciclo inteiro, exercitado contra a API real com um cookie jar — que é como o
navegador se comporta:

| Passo | Esperado | Obtido |
|---|---|---|
| Login | 200 + cookie `secure; httponly; samesite=lax; path=/api/auth` | **200** |
| Refresh | 200 com access token **novo** | **200**, token diferente |
| O cookie rotacionou | valor diferente do anterior | **sim** |
| Reuso do cookie **antigo** | 401 | **401 `sessao_invalida`** |
| O cookie **novo** depois do reuso | 401 — a família inteira cai | **401** |
| Refresh **sem** `Origin` | recusa | **401** |
| Refresh com `Origin` de outro site | recusa | **403** |
| Logout | 204 | **204** |
| Refresh **depois** do logout | 401 | **401** |
| Cookie após o logout | apagado | **apagado** |

A quinta linha é a que vale mais: detectar reuso e derrubar **toda a família** de
tokens é o que transforma um refresh vazado num incidente que termina, em vez de
num acesso permanente. Funciona em produção.

A sétima linha é a defesa de CSRF da Fase 11: o cookie é `SameSite=Lax`, mas a
verificação de `Origin` é a segunda tranca — e ela responde 403, não 401, porque
a credencial era válida e o que faltou foi procedência.

### 9.5 No navegador de verdade

Página aberta em `https://central-antifraude.vercel.app`:

- a aplicação chamou `/health/ready` na Function URL e renderizou
  **"API: Operacional"** e **"postgresql: Operacional"** — o CORS funciona do
  navegador, que é onde o header duplicado teria quebrado;
- a CSP permitiu a chamada (`connect-src` inclui `*.lambda-url.us-east-1.on.aws`);
- console sem erro de CORS, CSP ou rede;
- `/alertas` por URL direta devolveu 200 (rewrite de SPA) e **redirecionou para
  `/entrar`** por não haver sessão;
- os cinco cabeçalhos de segurança chegaram ao navegador.

**O que não foi exercido na interface:** o preenchimento do formulário de login e
o F5 subsequente. Não é limitação do produto — o ambiente deste agente proíbe
inserir senha em campo, e a verificação ficou pelo mecanismo, na tabela 9.4.

### 9.6 IAM efetivo

Políticas geradas pelo SAM, lidas da role real da API:

- `sqs:SendMessage*` preso ao ARN de cada fila, nomeadamente;
- `lambda:InvokeFunction` preso **ao despachante**, e não a tudo;
- `ssm:GetParameter*` preso a `parameter/portfolio/central-antifraude/producao`
  e aos filhos.

Uma folga que **não foi escolhida por nós**: o SAM inclui
`ssm:DescribeParameters` com `Resource: "*"`. A ação não lê valor nenhum — só
lista nomes — e a API do SSM não aceita recurso específico para ela. Fica
registrada como o que é: uma concessão da política gerenciada, não uma decisão.

Receber e apagar mensagem não aparecem em política nenhuma: vêm do gatilho de
SQS, que o SAM anexa à role da função que consome aquela fila.

### 9.7 Superfície pública

Um único endpoint alcançável da internet: a Function URL da API, com
`AuthType: NONE` — deliberado, porque a autenticação é da aplicação. As quatro
filas, as três funções de trabalho e os parâmetros do SSM não têm endpoint
público. Os três parâmetros são `SecureString` com `alias/aws/ssm`.

### 9.8 O defeito de segurança que o deploy revelou

O CORS estava declarado em **dois** lugares — Function URL e aplicação — e a
resposta simples voltava com `Access-Control-Allow-Origin` duplicado. Navegador
trata header CORS duplicado como falha.

Não era uma brecha: era o oposto, um bloqueio. Mas a lição é de segurança:
**duas autoridades para a mesma decisão não somam proteção, dividem
responsabilidade** — e a tentação seguinte teria sido afrouxar uma das duas para
"destravar". A autoridade agora é uma, e um teste impede a volta.

Verificado depois da correção: um header apenas; preflight 204; origem
desconhecida não recebe cabeçalho CORS nenhum.

---

## 10. O que este gate NÃO prova

Nomeado para não ser confundido com cobertura:

- **Não houve pentest.** É a Fase 15, e nada aqui substitui um.
- **Não houve teste de carga.** O rate limiting foi verificado num caminho; o
  comportamento sob volume não.
- **O formulário de login não foi preenchido na interface.** O mecanismo de
  sessão foi verificado ponta a ponta (tabela 9.4) e o CORS foi verificado no
  navegador, mas ninguém clicou em "Entrar" e apertou F5.
- **Isolamento entre tenants foi testado com um tenant só.** O 404 no recurso
  alheio prova o comportamento, mas a demo tem uma organização apenas; a
  cobertura multi-tenant de verdade continua sendo a da suíte de integração.
- **A retenção de log ainda não expirou nada.** Os grupos têm horas de vida.

---

## 11. Veredito

**Aprovado para preparação. Não aprovado para deploy automático** — deploy
continua exigindo autorização explícita (`CLAUDE.md` seção 109), e nenhuma foi
dada.
