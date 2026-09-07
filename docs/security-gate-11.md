# Security Gate 11 — Hardening da superfície construída

Data da execução: **2026-09-07**
Escopo: a fase que não acrescenta capacidade — ela revisa o que onze fases
construíram.

Testes em `tests/CentralAntifraude.IntegrationTests/SecurityGate11Tests.cs`,
`MatrizDeAutorizacaoTests.cs` e `IsolamentoCompletoTests.cs`, mais os dez gates
anteriores, que continuam rodando.

Documentos: [`threat-model.md`](threat-model.md) e
[`matriz-de-autorizacao.md`](matriz-de-autorizacao.md).

---

## A premissa desta fase

Segurança apareceu em todas as fases. O que **só** aparece olhando o conjunto
são três coisas:

1. **o modelo de deploy** — a Fase 14 coloca o frontend em um domínio e a API
   em outro, e CORS, cookie e `Origin` precisam concordar **antes** disso;
2. **os limites que faltavam** — `refresh` e emissão de credencial estão
   nomeados no `CLAUDE.md` seção 55 e não tinham limite próprio;
3. **o que uma fase futura vai quebrar** — revisar rota a rota funciona uma
   vez; o que não funciona é lembrar de revisar de novo.

O terceiro item é o que mudou de natureza aqui: a matriz de autorização deixou
de ser um documento e virou um **teste que lê o roteamento da aplicação real**.

---

## Itens exigidos pelo `ROADMAP.md`

| Item | Resultado | Onde |
|---|---|---|
| 11.2 Threat model | ✅ 7 fluxos, com ameaça, mitigação e teste por linha | [`threat-model.md`](threat-model.md) |
| 11.3 Autenticação | ✅ vida do token, `ClockSkew` zero, algoritmo fixo, rotação e reuso | `O_access_token_e_curto_e_sem_tolerancia_de_relogio` + Gate 1 |
| 11.4 Browser security | ✅ CORS validado no modelo de deploy planejado; `Origin`, cookie e cabeçalhos | `A_origem_declarada_e_autorizada_e_a_estranha_nao` |
| 11.5 Integrações | ✅ hash, rotação, revogação, replay, idempotência, log e **limite novo** | Gate 2 + `A_emissao_de_credencial_tem_limite_proprio` |
| 11.6 Authorization matrix | ✅ tabela verificada contra as rotas reais, 5 invariantes | `MatrizDeAutorizacaoTests` |
| 11.7 Input security | ✅ JSON estrito, mass assignment, tamanho, controle Unicode, XSS, SQLi, enum | `SecurityGate11Tests` + Gate 10 |
| 11.8 Multi-tenancy | ✅ 8 famílias, identificadores **reais**, conferido no banco | `IsolamentoCompletoTests` |
| 11.9 Dependências e secrets | ✅ `gitleaks`, `--vulnerable`, `npm audit`, revisão de log e exemplos | abaixo |
| 11.10 IA | ⚪ **nenhuma ação** — não há IA no produto, e o ROADMAP proíbe adicionar só para marcar item | — |

---

## O que foi corrigido nesta fase

Quatro lacunas concretas, e não revisão genérica:

### 1. Não havia CORS

Até aqui o `vite dev` fazia proxy e o navegador enxergava tudo na mesma origem.
A Fase 14 quebra isso. A política entrou agora, com a **mesma lista de origens**
que já defendia contra CSRF — duas listas sairiam de sincronia no primeiro
ajuste, e o sintoma seria o pior possível: o navegador aceitando a resposta e o
servidor recusando a operação.

Sem curinga: `AllowAnyOrigin` e `AllowCredentials` são incompatíveis por decisão
da especificação, e por um bom motivo — permitir credencial de qualquer origem
entrega a sessão a qualquer site. Lista vazia **desliga** o CORS, que é o estado
de desenvolvimento.

### 2. `refresh` e credenciais não tinham limite

Os dois estão nomeados no `CLAUDE.md` seção 55. `refresh` é anônimo por
natureza — a prova é o cookie —, e sem limite a rota vira um oráculo para
adivinhar valores. Emissão de credencial é a operação administrativa mais
sensível do produto: cada chamada devolve um segredo novo.

**Isso obrigou a mover o limitador no pipeline.** A partição da emissão de
credencial é a *organização da identidade*, e antes da autenticação
`HttpContext.User` está vazio — a partição cairia no IP e a cota viraria global,
deixando a conta comprometida de um cliente travar a operação dos outros. O
limitador agora roda depois de `UseAuthentication`, e há teste para o
comportamento por organização.

### 3. Nenhuma rota humana tinha teto de corpo

Só a ingestão declarava 8 KB; o resto herdava os 30 MB do Kestrel. Um corpo de
30 MB seria lido inteiro antes de qualquer validação.

O teto entrou em **três camadas**, e cada uma cobre o buraco da anterior:
`Content-Length` recusado no middleware (o caminho de 99% das requisições, e o
único que vale igual em produção e no host de teste), a feature
`IHttpMaxRequestBodySizeFeature` para corpo em `chunked`, e a opção do Kestrel
como rede do servidor real.

O status é `413`, e não `400`. A diferença importa para quem integra: `400`
manda depurar o JSON, `413` manda mandar menos.

### 4. A matriz de autorização não existia como verificação

Era conhecimento espalhado por dez gates. Agora é uma tabela declarada, e o
teste lê as rotas do `EndpointDataSource` da aplicação real e compara. **O
risco que isso fecha não é uma rota mal protegida — é uma rota nova.**

Cinco invariantes, das quais a menos óbvia é a mais útil: nenhuma rota de
escrita pode carregar `perfil:qualquer`, porque essa política inclui o Auditor,
cujo perfil é de leitura. Lendo a rota isoladamente, ninguém perceberia.

---

## 11.8 — Varredura cross-tenant

Um recurso de **cada família** é montado no tenant B pelo caminho real do
produto — nada é inserido direto no banco —, e cada identificador passa pela API
do tenant A.

| Família | Leitura | Escrita |
|---|---|---|
| Usuário | `404` | `404` |
| Integração | `404` | `404` |
| Credencial | — | `404` (rotação e revogação) |
| Transação | `404` | — (sem escrita humana) |
| Avaliação | `404` (via detalhe) | — |
| Alerta | fora da listagem | — |
| Caso | `404` | `404` (assumir, nota, resolução, associar) |
| Regra | `404` | `404` (publicar, ativar) |
| Perfil de risco | versões distintas por tenant | — |
| Backtest | `404` | `404` (cancelar) |
| Auditoria | conjuntos disjuntos | — (sem escrita) |

**O identificador é sempre real.** Um GUID sorteado responderia `404` de
qualquer jeito e o teste passaria pelo motivo errado — provaria apenas que o
recurso não existe.

**A resposta HTTP não basta.** Depois de todas as tentativas de escrita, o
estado do tenant B é lido do banco: caso ainda `Novo` e sem responsável, zero
notas, regra ainda ativa, integração ainda ativa, uma credencial só. Um `404`
devolvido *depois* de gravar seria pior do que um `200`.

---

## 11.7 — Entrada

| Verificação | Resultado |
|---|---|
| Campo desconhecido no corpo, em três rotas diferentes | `400` |
| `organizacaoId` e `status` por mass assignment | `400` |
| `candidato` no pedido de backtest | `400` |
| Byte nulo em texto humano | `400` |
| Marcação (`<script>`) em texto humano | recusado, nunca limpo (Gate 7) |
| SQLi em filtro e busca | zero resultado, tabela intacta (Gate 10) |
| Curinga de `LIKE` na busca | tratado como literal (Gate 10) |
| Enum por número | `400` (Gate 10) |
| Corpo acima de 64 KB | `413` |
| Corpo de 4 KB | chega à validação de domínio |

O último é o par do penúltimo: um teto que recusasse entrada legítima seria
igualmente um defeito. Uma nota de investigação tem até 4.000 caracteres.

---

## 11.9 — Dependências e segredos

| Verificação | Resultado |
|---|---|
| `gitleaks detect` (histórico) | nenhum |
| `gitleaks detect --no-git` (árvore) | nenhum |
| `dotnet list package --vulnerable` | nenhum, nos 7 projetos |
| `npm audit` | 0 |
| Resposta de erro em `Production` | sem pilha, sem `Npgsql`, sem string de conexão |
| Trilha de auditoria | 8 palavras proibidas ausentes (Gate 10) |
| Log de recusa de credencial | motivo sim, chave não (Gate 2) |

---

## O que este gate **não** cobre

Nomeado para não ser confundido com cobertura:

- **Rate limiting é por instância do processo.** Com várias instâncias em
  Lambda, o limite efetivo se multiplica. Tratamento distribuído exige estado
  compartilhado, que a arquitetura não tem — e não terá sem problema concreto.
  Fica registrado como limitação conhecida, e não como pendência.
- **Sem pentest.** É a Fase 15.
- **Sem teste de canal lateral por tempo no login.** A equalização é por
  construção; um teste confiável exigiria estatística sobre muitas amostras e
  seria intermitente no CI.
- **CORS e cookie cross-site testados contra a configuração, não contra o
  deploy.** O deploy é a Fase 14 — e a ordem foi deliberada, para chegar lá com
  o comportamento já verificado.
- **Sem CSP nem HSTS.** Pertencem a quem serve HTML, que a partir da Fase 14 é
  a Vercel. Repetir aqui uma política de conteúdo para respostas JSON que nunca
  são renderizadas seria configuração sem problema para resolver.
