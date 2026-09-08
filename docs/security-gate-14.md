# Security Gate 14 — Infraestrutura, custo e deploy

**Data:** 2026-09-08
**Escopo:** o que a Fase 14 acrescentou à superfície de ataque — template do
CloudFormation, funções Lambda, filas SQS, Function URL, Parameter Store e o
frontend hospedado.

**Nenhum recurso foi criado.** Este gate examina a infraestrutura declarada, e
não uma infraestrutura no ar. A distinção importa: metade das verificações
abaixo só pode ser confirmada de verdade depois do deploy, e isso está dito.

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

## 9. O que este gate NÃO prova

Nomeado para não ser confundido com cobertura:

- **Nenhum recurso existe.** Tudo acima é sobre infraestrutura declarada.
- **As roles IAM não foram exercidas.** Uma política insuficiente só aparece na
  primeira chamada real.
- **A leitura do Parameter Store não foi feita.** O caminho e o formato estão
  corretos no código; a permissão efetiva é assunto do deploy.
- **O CORS não foi testado contra um navegador.** Está declarado nos dois
  lugares e eles concordam, o que é diferente de funcionar.
- **Não houve pentest.** É a Fase 15.

---

## 10. Veredito

**Aprovado para preparação. Não aprovado para deploy automático** — deploy
continua exigindo autorização explícita (`CLAUDE.md` seção 109), e nenhuma foi
dada.
