# ROADMAP.md — Central Antifraude

> Plano de execução da **Central Antifraude v1.0.0**.
>
> Este arquivo define **quando** cada capacidade será construída, em qual ordem e quais critérios tornam cada fase concluída.
>
> O `CLAUDE.md` é a autoridade permanente sobre produto, arquitetura, segurança, autonomia e invariantes.
>
> Regra operacional:
>
> > **Uma autorização de fase = autorização para concluir a fase inteira.**
>
> Push, deploy e operações remotas continuam exigindo autorização explícita conforme o `CLAUDE.md`.

---

# 1. Objetivo do roadmap

Construir uma plataforma B2B capaz de:

1. receber transações de pagamentos digitais;
2. processá-las de forma idempotente;
3. avaliar risco imediatamente;
4. explicar cada sinal e decisão;
5. lidar corretamente com concorrência;
6. publicar efeitos assíncronos de forma confiável;
7. gerar alertas;
8. permitir investigação humana;
9. versionar regras e perfis;
10. executar backtests;
11. manter trilha auditável;
12. operar com observabilidade e segurança;
13. ser demonstrável publicamente como produto de portfólio;
14. chegar a uma release final `v1.0.0`.

---

# 2. Princípios de sequenciamento

A ordem das fases segue estas regras:

## 2.1 Corretude antes de volume de features

Primeiro:

- isolamento;
- idempotência;
- domínio;
- decisão;
- concorrência.

Depois:

- alertas;
- investigação;
- backtests;
- dashboard.

## 2.2 Infraestrutura assíncrona só depois do caminho síncrono correto

O caminho crítico deve funcionar corretamente antes de adicionar SQS.

Não usar mensageria para esconder inconsistências no fluxo principal.

## 2.3 Frontend cresce junto com o domínio

Não deixar toda a interface para uma fase final gigante.

Cada grande capacidade operacional deve chegar com sua experiência mínima de frontend quando fizer sentido.

## 2.4 Segurança é incremental

Cada fase possui seu próprio **Security Gate**.

Existe também hardening final, mas ele não substitui segurança durante a construção.

## 2.5 Produção só depois da base estar madura

Não fazer deploy público cedo apenas para “ver funcionando”.

Deploy vem depois de:

- fluxo principal;
- segurança mínima;
- testes relevantes;
- observabilidade;
- dados de demonstração.

---

# 3. Visão macro

| Fase | Nome | Resultado principal |
|---|---|---|
| 0 | Fundação Técnica | Repositório, arquitetura, CI e contratos base |
| 1 | Identidade e Multi-tenancy | Usuários, organizações, RBAC e isolamento |
| 2 | Integrações e Ingestão | API de transações + idempotência |
| 3 | Motor de Risco v1 | Regras determinísticas e score explicável |
| 4 | Avaliação Síncrona e Concorrência | Decisão transacional robusta sob simultaneidade |
| 5 | Backbone Assíncrono | Outbox + SQS + Inbox + DLQ |
| 6 | Alertas | Fila operacional derivada de avaliações |
| 7 | Casos e Investigação | Workflow humano auditável |
| 8 | Gestão e Versionamento de Regras | Draft, publicação e histórico imutável |
| 9 | Backtests | Simulação histórica usando o mesmo motor |
| 10 | Operação, Busca e Auditoria | Transações, filtros, auditoria e visão operacional |
| 11 | Segurança Aplicacional | Hardening de auth, integrações e superfícies |
| 12 | Observabilidade, Resiliência e Performance | Correlação, métricas, carga e falhas |
| 13 | Demo e UX Final | Seed narrativo, falso positivo e polish |
| 14 | Infraestrutura e Deploy | AWS/Vercel/Neon com custo validado |
| 15 | Validação Final e Release | Pentest, CI final, README, vídeo e `v1.0.0` |

---

# 4. Dependências principais

```text
Fase 0
  ↓
Fase 1
  ↓
Fase 2
  ↓
Fase 3
  ↓
Fase 4
  ↓
Fase 5
  ↓
Fase 6
  ↓
Fase 7
  ↓
Fase 8
  ↓
Fase 9
  ↓
Fase 10
  ↓
Fase 11
  ↓
Fase 12
  ↓
Fase 13
  ↓
Fase 14
  ↓
Fase 15
```

Algumas capacidades são incrementais e podem começar em uma fase e amadurecer depois.

Exemplo:

- auditoria nasce cedo para ações críticas;
- Fase 10 consolida consulta e experiência de auditoria;
- observabilidade básica existe desde Fase 0;
- Fase 12 faz o hardening operacional completo.

---

# FASE 0 — FUNDAÇÃO TÉCNICA

## 0.1 Objetivo

Criar uma base técnica limpa, reproduzível e testável antes de implementar regra de negócio relevante.

A fase deve terminar com:

> solução compilando, testes base executando, PostgreSQL de integração funcionando, frontend criado e CI inicial verde.

---

## 0.2 Backend

Criar estrutura inicial em .NET 10.

Direção sugerida:

```text
src/
  CentralAntifraude.Api/
  CentralAntifraude.Application/
  CentralAntifraude.Domain/
  CentralAntifraude.Infrastructure/

tests/
  CentralAntifraude.UnitTests/
  CentralAntifraude.IntegrationTests/
  CentralAntifraude.ArchitectureTests/
```

A estrutura pode ser ajustada se houver justificativa de coesão.

Não criar dezenas de projetos por módulo nesta fase.

---

## 0.3 Frontend

Criar:

- React;
- TypeScript;
- Vite;
- roteamento;
- cliente HTTP;
- estrutura de layouts;
- estratégia de server state;
- base de testes;
- lint/format se adotado.

Não criar telas fictícias completas ainda.

Criar somente:

- shell da aplicação;
- rota pública;
- rota autenticada placeholder;
- tratamento base de erro/loading.

---

## 0.4 Banco e integração

Preparar:

- EF Core;
- PostgreSQL;
- migrations;
- Testcontainers para testes de integração;
- configuração local segura;
- health check de banco.

Não usar SQLite como substituto.

---

## 0.5 Contratos transversais

Definir e testar padrões para:

- IDs internos;
- UTC;
- dinheiro/decimal;
- erros HTTP;
- Problem Details ou contrato equivalente;
- JSON;
- validação;
- correlation ID;
- paginação;
- ordenação;
- datas;
- enums serializados;
- cancellation tokens.

A escolha de UUID/ULID/GUID deve ser congelada nesta fase e documentada.

---

## 0.6 Arquitetura

Criar proteção inicial contra dependências indevidas.

Exemplo:

```text
Domain
  não depende de Infrastructure

Application
  não depende de Api

Infrastructure
  pode implementar contratos necessários

Api
  compõe aplicação
```

Adicionar architecture tests quando úteis.

---

## 0.7 CI inicial

Pipeline deve rodar, no mínimo:

- restore;
- build;
- backend tests;
- frontend install;
- frontend build;
- frontend tests;
- secret scanning inicial.

Não depender de serviço pago.

---

## 0.8 Documentação

Adicionar:

- `CLAUDE.md`;
- `ROADMAP.md`;
- README inicial curto;
- instruções de execução local;
- decisão arquitetural inicial do monólito modular.

Não escrever README final de marketing ainda.

---

## 0.9 Security Gate 0

Validar:

- secrets fora do repositório;
- `.env` ignorado;
- configuração de desenvolvimento sem credencial real;
- stack traces não expostos no contrato de produção;
- JSON/DTOs não vinculados diretamente a entidades;
- dependências sem vulnerabilidade crítica conhecida;
- secret scanning verde.

---

## 0.10 Testes mínimos

Cobrir:

- aplicação sobe;
- health básico;
- banco Testcontainers;
- migration inicial;
- contrato de erro;
- arquitetura;
- frontend smoke test.

---

## 0.11 Critérios de conclusão

- [x] .NET 10 compilando sem erro.
- [x] Frontend compilando sem erro.
- [x] PostgreSQL real funcionando em integração.
- [x] Testcontainers configurado.
- [x] Primeira migration reproduzível.
- [x] CI inicial verde.
- [x] Security Gate 0 verde.
- [x] README de setup suficiente para outro desenvolvedor executar.
- [x] Nenhuma feature de domínio artificial criada apenas para testar arquitetura.
- [x] Commits locais coerentes.

---

## 0.12 Resultado da Fase 0

**Concluída em 2026-09-03.**

### Evidências

| Critério | Como foi verificado |
|---|---|
| Build backend | `dotnet build` em `Release`, **0 erros e 0 avisos**, com `TreatWarningsAsErrors` ligado |
| Build frontend | `npm run build` (inclui `tsc -b` com `strict: true`) |
| PostgreSQL real | 12 testes de integração contra container PostgreSQL 17 |
| Migration | `InicialBaseline` aplicada, reaplicada sem duplicar, e sem alteração de modelo pendente |
| Testes | 92 no backend (65 unitários + 15 de arquitetura + 12 de integração) e 23 no frontend — **115 no total, todos verdes** |
| Security Gate 0 | [`docs/security-gate-0.md`](docs/security-gate-0.md) |
| Secret scanning | gitleaks 8.30.1: `no leaks found`, com a configuração validada por mutação |
| Dependências | `dotnet list package --vulnerable`: nenhuma; `npm audit`: 0 vulnerabilidades |

### Decisões congeladas

| Decisão | Registro |
|---|---|
| Monólito modular em quatro projetos | `docs/adr/0001-monolito-modular.md` |
| **Identificadores internos: UUID versão 7** | `docs/adr/0002-identificadores-internos.md` |
| PostgreSQL real em teste, snake_case, migration base vazia | `docs/adr/0003-postgresql-e-testes-de-integracao.md` |
| UTC, dinheiro, erros, JSON estrito, correlação, paginação, ordenação, cancellation | `docs/adr/0004-contratos-transversais.md` |

Três decisões são reforçadas em tempo de compilação por `src/BannedSymbols.txt`:
`DateTime.UtcNow`, `DateTime.Now` e `Guid.NewGuid()` quebram o build em `src/`.

### Verificado por mutação

Dois guardas foram testados quebrando-os de propósito, para não passarem pelo
motivo errado:

- teste de arquitetura: adicionar pacote de persistência ao `Domain` faz a
  suíte falhar com mensagem explícita;
- secret scanning: um arquivo com credencial Neon realista produz 2 achados.

### Fora de escopo, deliberadamente

Nomeado para não ser confundido com cobertura:

- **Nenhuma entidade de domínio.** A migration inicial é vazia — a Fase 0 não
  inventa entidade para ter o que testar.
- **Sem CORS.** Em desenvolvimento o `vite dev` faz proxy; a política real
  depende do modelo de deploy, decidido na Fase 14.
- **Sem autenticação.** `useSessao()` no frontend é um contrato placeholder;
  a rota protegida existe e redireciona, mas nada é acessível ainda.

### Débito técnico não bloqueante

1. **CI não executado.** O workflow foi escrito e cada comando dele foi rodado
   localmente, mas o GitHub Actions só roda depois do primeiro `push` — que
   não foi autorizado. O `.slnx` (formato novo de solução do SDK 10) é o ponto
   com maior chance de atrito na primeira execução.
2. **`__EFMigrationsHistory` mantém o nome em PascalCase** enquanto suas
   colunas viraram snake_case. É o EF Core que define o nome dessa tabela, fora
   do alcance da convenção. Sem impacto funcional.
3. **`react-router` resolveu para 7.18.3** e não para a 8, disponível no
   registro. O npm aplicou uma restrição de peer dependency. A v7 é estável e
   suficiente; vale reavaliar na Fase 1, quando a navegação autenticada chegar.

---

# FASE 1 — IDENTIDADE, ORGANIZAÇÕES E MULTI-TENANCY

## 1.1 Objetivo

Construir a fundação B2B de identidade e isolamento.

Ao final:

> usuários humanos entram na plataforma com perfil definido e só enxergam dados do próprio tenant.

---

## 1.2 Domínio

Implementar:

- Organização/Tenant;
- Usuário;
- vínculo usuário-organização;
- perfil/role.

Perfis:

- Administrador;
- SupervisorDeFraude;
- AnalistaDeFraude;
- Auditor.

Evitar sistema genérico de permissões arbitrárias nesta fase.

RBAC fechado é suficiente para v1.

---

## 1.3 Autenticação humana

Implementar fluxo inicial de:

- login;
- access token;
- refresh token;
- logout;
- refresh rotation;
- invalidação/revogação conforme desenho;
- detecção de reuse quando aplicável.

Não usar localStorage para refresh token sensível.

A solução final deve respeitar deploy cross-origin planejado.

---

## 1.4 Tenant context

Toda requisição autenticada deve possuir tenant derivado da identidade.

Não aceitar `TenantId` de payload ou query como autoridade.

Criar abstração explícita de contexto atual.

---

## 1.5 Persistência

Criar constraints e relacionamentos seguros.

Testar:

- usuário de tenant A;
- recurso tenant B;
- ID conhecido de outro tenant.

Resultado esperado:

> recurso não acessível e existência não revelada.

---

## 1.6 Frontend

Criar:

- login;
- restauração de sessão;
- shell autenticado;
- navegação por perfil;
- logout;
- proteção de rotas;
- estados de sessão expirada.

Menu inicial pode conter rotas futuras desabilitadas apenas se isso não confundir a demo.

Preferência: exibir somente o que existe.

---

## 1.7 Auditoria inicial

Registrar ações sensíveis:

- login relevante quando fizer sentido;
- criação/alteração de usuário;
- mudança de perfil;
- revogação de sessão administrativa quando existir.

Não logar token.

---

## 1.8 Security Gate 1

Testar:

- JWT inválido;
- JWT expirado;
- refresh rotation;
- reuse;
- role escalation;
- alteração de role via payload indevido;
- cross-tenant;
- usuário inativo;
- enum/claim forjado;
- endpoints administrativos por perfil errado;
- sessão após refresh/F5.

---

## 1.9 Critérios de conclusão

- [x] Login funcional.
- [x] Refresh funcional.
- [x] Logout funcional.
- [x] RBAC no backend.
- [x] Tenant derivado da autenticação.
- [x] Cross-tenant coberto por integração.
- [x] Frontend restaura sessão corretamente.
- [x] Auditor não possui escrita indevida.
- [x] Administrador possui somente escopo previsto.
- [x] Security Gate 1 verde.
- [x] CI verde.

---

## 1.10 Resultado da Fase 1

**Concluída em 2026-09-03.**

### Evidências

| Critério | Como foi verificado |
|---|---|
| Build backend | `dotnet build` em `Release`, **0 erros e 0 avisos**, com `TreatWarningsAsErrors` |
| Build frontend | `npm run build` (`tsc -b` com `strict: true`), lint e formatação limpos |
| Login / refresh / logout | 34 testes em `SecurityGate1Tests`, contra a API real em ambiente `Production` |
| RBAC | os 3 perfis não-Administrador recebem 403 em `/api/usuarios` |
| Tenant da autenticação | claim `org` assinada; nenhum parâmetro, cabeçalho ou rota troca de organização |
| Cross-tenant | 9 testes em `IsolamentoDeTenantTests`, com **dois tenants reais** no banco |
| Sessão no F5 | verificado nos testes e no navegador real, com Chrome |
| Testes | 136 unitários + 15 arquitetura + 56 integração + 33 frontend = **240, todos verdes** |
| Security Gate 1 | [`docs/security-gate-1.md`](docs/security-gate-1.md) |
| Migration | `IdentidadeEMultiTenancy` aplicada em PostgreSQL real, sem alteração de modelo pendente |
| Dependências | `dotnet list package --vulnerable`: nenhuma; `npm audit`: 0 |

### Decisões congeladas

| Decisão | Registro |
|---|---|
| E-mail único global; um usuário por organização | `docs/adr/0005-identidade-e-multi-tenancy.md` |
| Isolamento em duas camadas: filtro nomeado do EF + testes com dois tenants | `docs/adr/0005` |
| Recurso de outro tenant responde **404**, não 403 | `docs/adr/0005` |
| Prefixo `/api` nas rotas de aplicação | `docs/adr/0005` |
| Access token em memória; refresh em cookie HttpOnly com `Path` restrito | `docs/adr/0006-sessao-humana.md` |
| Rotação com família e derrubada em caso de reuso | `docs/adr/0006` |
| **PBKDF2-HMAC-SHA512, 220.000 iterações** (fonte: OWASP, 2026-09-03) | `docs/adr/0006` |
| CSRF por verificação de `Origin` | `docs/adr/0006` |

### Quatro defeitos reais encontrados e corrigidos

Cada um teria virado incidente; os dois últimos só o **navegador real** revelou:

1. **Corpo inválido virava 400 com corpo vazio em produção.**
   `ThrowOnBadRequest` só é `true` em Development por padrão — o contrato de
   erro quebrava justamente onde mais importa.
2. **`/api/auth/eu` devolvia 404 com token válido.** O handler JWT remapeava a
   claim `sub`; a autorização passava e só a identidade sumia.
3. **Login pelo navegador respondia 403.** O proxy do `vite dev` faz o `Origin`
   (`:5174`) não bater com o host da API (`:5175`). O `curl` não revela, porque
   não envia `Origin`.
4. **F5 em `/usuarios` devolvia JSON da API.** Rota do SPA e endpoint da API
   colidiam no mesmo caminho.

### Fora de escopo, deliberadamente

- **Sem CORS real** — o `vite dev` faz proxy; a política depende do deploy
  cross-site, decidido na Fase 14 e desenhado na 11.
- **Sem consulta de auditoria** — a trilha é gravada e testada; a tela é a
  Fase 10.
- **Rate limiting só no login, por IP** — protege força bruta em uma conta,
  não *password spraying* distribuído. Superfície da Fase 11.

### Débito técnico não bloqueante

1. **CI ainda não executado.** Cada comando do workflow roda localmente, mas o
   GitHub Actions só roda após o primeiro `push`, que não foi autorizado.
2. **Duas abas renovando ao mesmo tempo** podem disparar a detecção de reuso e
   derrubar a sessão. Não observado — a renovação acontece perto da expiração,
   não simultaneamente. É a primeira hipótese a investigar se aparecer relato
   de "fui deslogado do nada".
3. **Equalização de tempo no login é por construção, não medida.** Um teste de
   temporização confiável exigiria estatística sobre muitas amostras e seria
   intermitente no CI.
4. **Sem tela de criação/edição de usuários no frontend.** A API está completa
   e testada; a interface administrativa tem apenas a listagem, que é o que o
   ROADMAP 1.6 pede nesta fase.

---

# FASE 2 — INTEGRAÇÕES E INGESTÃO DE TRANSAÇÕES

## 2.1 Objetivo

Criar a fronteira máquina-a-máquina do produto.

Ao final:

> uma integração autenticada consegue enviar uma transação fictícia de pagamento e o sistema a persiste exatamente uma vez.

Ainda não é obrigatório produzir score completo nesta fase.

---

## 2.2 Integração

Implementar entidade/configuração de integração.

Cada integração deve possuir:

- ID interno;
- tenant;
- nome;
- estado ativo/inativo;
- credencial própria;
- data de criação;
- revogação/rotação quando implementada.

Credencial bruta deve ser exibida somente no momento seguro definido.

Persistir hash/representação adequada, não secret reutilizável em texto puro.

---

## 2.3 Autenticação máquina-a-máquina

Criar endpoint de ingestão autenticado por credencial de integração.

O tenant deve vir da credencial.

Não aceitar:

```json
{
  "tenantId": "..."
}
```

como autoridade.

---

## 2.4 Contrato da transação

Definir DTO explícito contendo apenas informações necessárias.

Campos prováveis:

- `externalTransactionId`;
- `occurredAt`;
- `amount`;
- `currency`;
- `customerExternalId`;
- `paymentInstrumentRef`;
- `deviceFingerprint`;
- informação geográfica permitida;
- atributos derivados/controlados;
- metadados fechados quando necessários.

Não aceitar JSON arbitrário ilimitado.

---

## 2.5 Minimização de dados

Não receber/persistir:

- PAN;
- CVV;
- senha;
- conta bancária real desnecessária.

Se IP bruto fizer parte do contrato inicial:

- derivar o necessário;
- HMAC/fingerprint quando aplicável;
- evitar persistência permanente do valor bruto.

---

## 2.6 Idempotency Key

Exigir/implementar idempotência conforme contrato.

Garantias:

```text
Tenant + Integration + IdempotencyKey
```

e:

```text
Tenant + Integration + ExternalTransactionId
```

com constraints no banco.

---

## 2.7 Fingerprint do request

Definir forma determinística de reconhecer:

> mesma chave + mesmo payload

versus:

> mesma chave + payload diferente.

Canonicalização deve ser testável.

Não depender da ordem textual do JSON.

---

## 2.8 Replay

Implementar semântica:

- mesma key + mesmo conteúdo → resposta original;
- mesma key + conteúdo diferente → `409`;
- mesmo external ID equivalente → comportamento idempotente;
- mesmo external ID conflitante → `409`.

---

## 2.9 Frontend administrativo

Adicionar gestão mínima de integrações para Administrador:

- listar;
- criar;
- revogar;
- visualizar status;
- gerar/rotacionar credencial quando previsto.

Não exibir credencial secreta novamente após o ponto permitido.

---

## 2.10 Security Gate 2

Testar:

- API key inválida;
- integração revogada;
- tenant injection;
- mass assignment;
- payload desconhecido;
- payload grande;
- moeda inválida;
- valor inválido;
- timestamp inválido;
- replay;
- idempotency key collision;
- brute force/rate limit inicial;
- secret em logs.

---

## 2.11 Testes de idempotência

Obrigatórios:

### Sequencial

Mesmo request N vezes:

> uma transação.

### Concorrente

Mesmo request em múltiplas tasks:

> uma transação.

### Payload conflitante

Mesma key, conteúdo diferente:

> conflito.

### External ID conflitante

Nova key, mesmo ID com conteúdo diferente:

> conflito.

---

## 2.12 Critérios de conclusão

- [x] Integrações possuem autenticação separada de usuários.
- [x] Tenant deriva da credencial.
- [x] Endpoint de ingestão existe.
- [x] Idempotency Key implementada.
- [x] Constraint de negócio implementada.
- [x] Replay determinístico.
- [x] Teste concorrente verde.
- [x] PII financeira proibida.
- [x] Gestão administrativa mínima funcional.
- [x] Security Gate 2 verde.
- [x] CI verde.

---

## 2.13 Resultado da Fase 2

**Concluída em 2026-09-04.**

### Evidências

| Critério | Como foi verificado |
|---|---|
| Build backend | `dotnet build` em `Release`, **0 erros e 0 avisos** |
| Build frontend | `npm run build`, lint e formatação limpos |
| Autenticação separada | esquema `ApiKey` próprio; token humano na ingestão → 401, API key em rota humana → 401 |
| Tenant da credencial | teste confere no banco que a transação pertence à organização da chave |
| Replay determinístico | os 4 cenários do 2.11, mais 2 extras |
| **Teste concorrente** | 20 requisições simultâneas → **1× 201, 19× 200, 1 linha no banco** |
| PII financeira | contrato sem campo de cartão; detector de PAN por Luhn; IP só como HMAC |
| Gestão administrativa | tela de integrações com criação, rotação, revogação e ativação |
| Testes | 225 unitários + 15 arquitetura + 95 integração + 40 frontend = **375, todos verdes** |
| Security Gate 2 | [`docs/security-gate-2.md`](docs/security-gate-2.md) |
| Migration | `IntegracoesEIngestao` aplicada em PostgreSQL real, sem alteração pendente |
| Dependências | `dotnet list package --vulnerable`: nenhuma; `npm audit`: 0 |

### Decisões congeladas

| Decisão | Registro |
|---|---|
| **Idempotência em três camadas** (consulta, constraint, nova consulta) | `docs/adr/0007-ingestao-e-idempotencia.md` |
| Fingerprint canônico do conteúdo, insensível a ordem e formatação | `docs/adr/0007` |
| Credencial `caf_{publico}_{segredo}`, com esquema HTTP `ApiKey` | `docs/adr/0007` |
| Credencial como entidade separada, para rotação sem interrupção | `docs/adr/0007` |
| Contrato fechado, sem metadados livres | `docs/adr/0007` |
| IP nunca persistido — só HMAC com chave secreta | `docs/adr/0007` |
| Limites de sanidade separados de regras de risco | `docs/adr/0007` |

### Defeito real encontrado e corrigido

**O separador da credencial colidia com o alfabeto do segredo.** A chave é
`caf_{id}_{segredo}` e o segredo é base64url — alfabeto que inclui `_`. Um
`Split('_')` sem limite quebrava a chave em quatro pedaços e a recusava.

Como cerca de metade das chaves sorteadas contém `_`, **o defeito aparecia em
metade das execuções**: uma credencial emitida podia simplesmente nunca
autenticar, e o log não dizia por quê.

Corrigido com `Split('_', 3)` nos dois pontos que decompõem a chave. O teste
de regressão gera 500 chaves e exige que todas sejam interpretáveis — não
depende de sorte. O handler também ganhou log de Debug com o motivo da
recusa, sem o qual a investigação seguiria às cegas.

### Fora de escopo, deliberadamente

- **Sem score, sinais ou decisão.** A resposta da ingestão é um recibo; a
  decisão de risco entra na Fase 4, quando a avaliação passa a fazer parte da
  mesma operação.
- **Sem `SERIALIZABLE`.** A idempotência desta fase é garantida por restrição
  única, que basta para "uma transação por chave". O isolamento transacional
  da avaliação é da Fase 4.
- **Sem tela operacional de transações.** A listagem existe para confirmar a
  ingestão; a tela com score e sinais é da Fase 3.

### Débito técnico não bloqueante

1. **CI ainda não executado.** Cada comando roda localmente, mas o GitHub
   Actions só roda após o primeiro `push`, que não foi autorizado.
2. **Verificação visual no navegador não foi possível** nesta fase: a extensão
   do Chrome desconectou no meio da execução. As rotas do SPA foram conferidas
   por HTTP (todas devolvem HTML, não JSON) e a interface tem 40 testes com
   Testing Library — mas o teste visual das telas novas fica pendente.
3. **Rate limiting é por instância do processo.** Com várias instâncias em
   Lambda o limite efetivo se multiplica. Tratamento distribuído é da Fase 11.
4. **Detector de PAN tem falso positivo conhecido**: identificador puramente
   numérico de 13 a 19 dígitos que passe no Luhn é recusado. Assumido de
   propósito, com a saída documentada na mensagem de erro.

---

# FASE 3 — MOTOR DE RISCO DETERMINÍSTICO V1

## 3.1 Objetivo

Criar o primeiro motor real da Central Antifraude.

Ao final:

> uma transação pode ser avaliada com um conjunto versionado de regras e gerar score, decisão e sinais explicáveis.

---

## 3.2 Modelo de risco

Implementar:

- `RiskEvaluation`;
- `RiskSignal`;
- `RiskProfile`;
- `RiskProfileVersion`;
- `Rule`;
- `RuleVersion`.

Nesta fase, perfis/regras podem ser provisionados por seed/configuração controlada.

A administração completa vem depois.

---

## 3.3 Catálogo inicial de regras

Implementar um conjunto pequeno, mas significativo.

Sugestão inicial:

1. `VelocidadePorCliente`;
2. `NovoDispositivo`;
3. `ValorAcimaDoHistorico`;
4. `DivergenciaGeografica`;
5. `ContaOuClienteRecente` somente se os dados suportarem;
6. `RepeticaoDeValor` se houver justificativa no modelo.

Não implementar dezena de regras.

Cada regra nova deve adicionar comportamento demonstrável.

---

## 3.4 Pesquisa das regras

Antes de formalizar a semântica de cada regra:

- confirmar a ideia em fonte confiável quando apresentada como prática antifraude real;
- documentar a referência;
- separar prática real de threshold fictício.

Exemplo:

```text
Prática:
velocity checks são usados para identificar múltiplas tentativas em janela curta.

Configuração demo:
5 tentativas em 5 minutos = +30 pontos.
```

---

## 3.5 Risk Context

Criar modelo explícito do contexto fornecido às regras.

Não permitir que cada evaluator faça qualquer consulta ao banco por conta própria.

Preferir:

```text
carregar contexto
  ↓
montar RiskContext
  ↓
executar regras
```

Isso melhora:

- determinismo;
- teste;
- performance;
- backtest futuro.

---

## 3.6 Explicabilidade

Cada sinal deve persistir:

- tipo;
- versão;
- regra/version;
- pontos;
- dados explicativos permitidos;
- texto determinístico ou dados suficientes para apresentá-lo.

Não depender de IA para explicar o score.

---

## 3.7 Perfil e thresholds

Implementar decisão:

```text
score → threshold da versão do perfil → decisão
```

Score limitado a `0..100`.

Garantir que configuração inválida não possa ser publicada/seedada.

---

## 3.8 Snapshot histórico

Uma avaliação deve manter referência imutável às versões utilizadas.

Alterações futuras não podem mudar a explicação antiga.

---

## 3.9 Frontend

Criar:

### Lista básica de transações

Mostrar:

- horário;
- valor;
- cliente fictício;
- status de avaliação;
- score;
- decisão.

### Detalhe da transação

Mostrar:

- dados principais;
- score;
- decisão;
- sinais;
- contribuição de pontos;
- versão do perfil;
- horário da avaliação.

Interface deve começar a parecer ferramenta operacional.

---

## 3.10 Security Gate 3

Validar:

- usuário não altera score pelo frontend;
- usuário não envia decisão;
- campos internos rejeitados;
- avaliação de tenant A não acessível por B;
- rule config controlado;
- ausência de execução arbitrária;
- sem SQL/DSL configurável.

---

## 3.11 Testes mínimos

Cada tipo de regra deve possuir:

- cenário não acionado;
- limite inferior;
- limite exato;
- acima do limite;
- dados ausentes;
- casos temporais;
- determinismo;
- tenant correto quando depender de histórico.

Também testar:

```text
mesma entrada + mesmo contexto + mesma versão
→ mesmo resultado
```

---

## 3.12 Critérios de conclusão

- [x] Motor determinístico criado.
- [x] Catálogo inicial de regras implementado.
- [x] Score 0..100.
- [x] Decisão derivada de profile version.
- [x] Sinais persistidos e explicáveis.
- [x] Versões registradas.
- [x] Transação antiga não depende de config atual.
- [x] Detalhe da transação funcional no frontend.
- [x] Fontes das práticas reais documentadas.
- [x] Security Gate 3 verde.
- [ ] CI verde — *pendente do primeiro `push`, que não foi autorizado. Cada passo do workflow foi executado localmente e está verde.*

---

## 3.13 Resultado da Fase 3

**Concluída em 2026-09-04.**

### Evidências

| Critério | Como foi verificado |
|---|---|
| Build backend | `dotnet build -c Release --no-incremental`, **0 erros e 0 avisos** |
| Build frontend | `npm run build`, `oxlint` e `prettier --check` limpos |
| Determinismo | mesma transação + mesmo contexto + mesma versão → score, decisão, ordem, versões **e texto de cada explicação** idênticos |
| Score 0..100 | 4 regras de 40 pontos somam 160 → score 100, com `somaBrutaDosPontos` e `scoreFoiLimitado` visíveis na tela |
| Decisão pela versão do perfil | limiares testados nas quatro bordas (39/40/69/70) |
| Sinais explicáveis | cada sinal grava tipo, pontos, texto determinístico, evidência numérica e a **versão exata da regra** |
| Explicabilidade histórica | FKs `RESTRICT` de avaliação → versão de perfil e de sinal → versão de regra: o banco recusa apagar o que sustenta uma decisão |
| Uma avaliação por transação | 20 requisições simultâneas → **1× 201, 19× 200, 1 avaliação no banco** |
| Retry não recalcula | `avaliadaEm` e `score` idênticos entre a primeira resposta e o retry |
| Isolamento de histórico | mesmo `clienteExternoId` em dois tenants; rajada em A não produz sinal na primeira transação de B |
| Fontes | U.S. Payments Forum (2020), citação por regra, em [`docs/adr/0008`](docs/adr/0008-motor-de-risco.md) |
| Testes | 281 unitários + 15 arquitetura + 123 integração + 45 frontend = **464, todos verdes** |
| Security Gate 3 | [`docs/security-gate-3.md`](docs/security-gate-3.md) |
| Migration | `MotorDeRisco` aplicada em PostgreSQL real; `has-pending-model-changes` sem alteração pendente |
| Dependências | `dotnet list package --vulnerable`: nenhuma; `npm audit`: 0 |
| Segredos | `gitleaks detect`: nenhum |

### Decisões congeladas

| Decisão | Registro |
|---|---|
| **Motor é função pura** — sem banco, relógio, rede, aleatoriedade ou IA | `docs/adr/0008-motor-de-risco.md` |
| Contexto histórico carregado de uma vez, limitado em janela e quantidade | `docs/adr/0008` |
| Catálogo fechado e tipado; configuração é dado, nunca expressão | `docs/adr/0008` |
| Janelas ancoradas em `OccurredAt`, intervalo semiaberto, transação avaliada contada | `docs/adr/0008` |
| Ausência de dado nunca vira sinal de risco | `docs/adr/0008` |
| Avaliação na mesma gravação da transação; retry lê, não recalcula | `docs/adr/0008` |
| Catálogo padrão provisionado junto da organização | `docs/adr/0008` |
| País declarado pela integração, sem GeoIP | `docs/adr/0008` |
| Limiares 40/70, escolhidos para que uma regra sozinha nunca bloqueie | `docs/adr/0008` |

### Defeitos reais encontrados e corrigidos

**1. O horário mudava ao passar pelo banco.** O `DateTimeOffset` do .NET conta
em ticks de 100 ns; o `timestamptz` do PostgreSQL guarda microssegundos. A
resposta da ingestão devolvia `avaliadaEm` da memória na primeira requisição e
do banco no retry — **o mesmo pedido retornava dois horários diferentes**, com
9 ticks de diferença.

Corrigido em `Instante.Normalizar`, aplicado no relógio do sistema e nos tempos
da transação: o que entra na memória é exatamente o que o banco devolve.
Truncar, e não arredondar, porque truncar é o que o driver faz na gravação.
Quatro testes cobrem a regra.

**2. A suíte do frontend saía com código 1 mesmo com tudo verde.** Um
`mockResolvedValue(sessaoValida())` da Fase 1 reaproveitava a **mesma instância
de `Response`** entre chamadas. O corpo de um `Response` só pode ser lido uma
vez, então a segunda leitura estourava `Body has already been read` — um erro
que não reprovava teste nenhum, mas fazia o `vitest run` sair com código 1.

O CI teria ficado vermelho no primeiro `push` sem que nenhum teste falhasse.
Corrigido com uma resposta nova a cada chamada, com o motivo documentado no
arquivo.

### Fora de escopo, deliberadamente

- **Sem `SERIALIZABLE`.** A garantia desta fase é a restrição única de avaliação
  por transação. O isolamento transacional da leitura de contexto sob
  concorrência é a Fase 4, e é lá que a regra de velocidade vira teste de
  concorrência de verdade.
- **Sem gestão de regras.** Não existe rota que crie, edite ou publique regra ou
  perfil — e há teste que verifica a ausência dessas rotas. Rascunho, backtest e
  publicação são a Fase 8.
- **Sem alerta.** Score alto ainda não gera alerta nem caso. Fases 6 e 7.
- **Sem reavaliação retroativa.** Um evento atrasado é avaliado com o contexto
  dele, e avaliações passadas não são recalculadas (`CLAUDE.md` seção 16).

### Débito técnico não bloqueante

1. **CI ainda não executado.** Cada comando do workflow roda localmente e está
   verde, mas o GitHub Actions só roda após o primeiro `push`, que continua não
   autorizado.
2. **Verificação visual no navegador segue pendente.** A extensão do Chrome está
   desconectada nesta máquina desde a Fase 2. As telas novas — lista com score,
   detalhe com sinais e catálogo de regras — têm testes de componente, mas não
   inspeção visual. Mesma dívida da fase anterior.
3. **Sem índice novo para o contexto histórico.** A consulta usa o índice
   `(organizacao, cliente_externo_id, ocorrida_em)` já existente. A Fase 4 mede
   o plano real antes de acrescentar qualquer outro (`CLAUDE.md` seção 46).
4. **Sem rate limit próprio nas consultas humanas.** `/api/transacoes` e
   `/api/regras` não têm limite dedicado; não há operação cara nem enumerável
   ali hoje. A Fase 10 revisita quando o painel agregar dados.

---

# FASE 4 — AVALIAÇÃO SÍNCRONA E CONCORRÊNCIA

## 4.1 Objetivo

Unificar ingestão + contexto + avaliação em uma operação síncrona robusta.

Ao final:

> `POST` de uma nova transação retorna imediatamente `Permitir`, `Revisar` ou `Bloquear`, mesmo sob concorrência relevante.

Esta é uma das fases técnicas mais importantes do projeto.

---

## 4.2 Operação crítica

A operação deve abranger corretamente:

```text
validar
 ↓
garantir idempotência
 ↓
persistir/registrar transação
 ↓
ler contexto relevante
 ↓
executar motor
 ↓
persistir avaliação
 ↓
persistir sinais
 ↓
preparar evento
 ↓
commit
```

A introdução do evento persistido pode ser concluída nesta fase como modelo local; publicação vem na Fase 5.

---

## 4.3 SERIALIZABLE

Aplicar isolamento `SERIALIZABLE` onde necessário para preservar regras dependentes de simultaneidade.

Não aplicar indiscriminadamente a todo endpoint.

Documentar o boundary transacional.

---

## 4.4 Retry explícito

Implementar retry da operação completa em falha de serialização reconhecida.

Requisitos:

- detectar condição apropriada;
- rollback completo;
- novo contexto/transação;
- limite pequeno;
- logging sem dados sensíveis;
- métrica futura preparada;
- nenhuma política global genérica.

---

## 4.5 Regra de velocidade sob concorrência

Criar teste real com múltiplas transações simultâneas para o mesmo cliente/contexto.

A semântica precisa ser definida e reproduzível.

Exemplo de requisito:

> as decisões devem refletir uma ordem serial válida, sem perda silenciosa de transações do contexto.

Não exigir que todas as requests enxerguem o futuro.

Exigir resultado equivalente a alguma execução serial válida.

---

## 4.6 Idempotência + avaliação

Replay não pode recalcular com regras novas.

Se uma transação já foi avaliada:

> replay devolve o resultado original daquela avaliação.

Essa é uma invariante crítica.

---

## 4.7 Evento atrasado

Formalizar e testar:

- `OccurredAt`;
- `ReceivedAt`;
- `EvaluatedAt`.

Transação atrasada não reescreve avaliações históricas.

---

## 4.8 Performance baseline

Medir localmente:

- latência média;
- p95/p99 de cenário controlado;
- número de queries;
- queries mais caras.

Não definir SLA bancário fictício.

Registrar apenas baseline do ambiente de teste.

---

## 4.9 Security Gate 4

Testar:

- race para duplicar transação;
- race para duplicar avaliação;
- serialização/retry;
- replay depois de nova rule version;
- timeout cliente seguido de retry;
- entrada maliciosa em concorrência;
- tenant isolation durante queries históricas.

---

## 4.10 Critérios de conclusão

- [x] Ingestão retorna decisão síncrona.
- [x] Avaliação faz parte do boundary transacional correto.
- [x] SERIALIZABLE usado somente onde justificado.
- [x] Retry completo testado.
- [x] Replay não recalcula avaliação.
- [x] Teste concorrente de velocidade verde.
- [x] Transações simultâneas não geram duplicidade.
- [x] Evento atrasado possui semântica documentada.
- [x] Baseline de performance registrado.
- [x] Security Gate 4 verde.
- [ ] CI verde — *pendente do primeiro `push`, que não foi autorizado. Cada passo do workflow foi executado localmente e está verde.*

---

## 4.11 Resultado da Fase 4

**Concluída em 2026-09-04.**

### Evidências

| Critério | Como foi verificado |
|---|---|
| Build backend | `dotnet build -c Release --no-incremental`, **0 erros e 0 avisos** |
| Build frontend | `npm run build`, `oxlint` e `prettier --check` limpos |
| Boundary transacional | `SERIALIZABLE` **somente** na ingestão; demais rotas no padrão |
| Retry deliberado | operação inteira refeita, `ChangeTracker` limpo, espera com sorteio, limite 8 |
| **Velocidade sob concorrência** | 6 simultâneas do mesmo cliente → contagens de janela **{4,5,6}** nos sinais e 3 abaixo do limite: prova exata de ordem serial válida |
| Duplicidade sob corrida | 20 simultâneas idênticas → **1× 201, 19× 200, 1 transação, 1 avaliação, 1 evento** |
| Replay após regra nova | mesmo score, mesma decisão, mesmo `avaliadaEm`, ainda na **versão 1** do perfil |
| Regra nova vale para o futuro | transação posterior à publicação usa a **versão 2** |
| Evento atrasado | três tempos distintos; avaliações anteriores **byte a byte iguais** depois da chegada da atrasada |
| Outbox | evento na mesma transação; replay não gera segundo evento; conteúdo sem instrumento, dispositivo ou IP |
| Baseline | p50 **15,4 ms**, p95 **28,7 ms**, p99 **39,9 ms**, **8 comandos SQL** por ingestão — [`docs/baseline-de-performance.md`](docs/baseline-de-performance.md) |
| Testes | 295 unitários + 15 arquitetura + 145 integração + 45 frontend = **500, todos verdes** |
| Security Gate 4 | [`docs/security-gate-4.md`](docs/security-gate-4.md) |
| Migration | `OutboxDeEventos` aplicada em PostgreSQL real; `has-pending-model-changes` sem alteração pendente |
| Dependências | `dotnet list package --vulnerable`: nenhuma; `npm audit`: 0 |
| Segredos | `gitleaks detect`: nenhum |

### Decisões congeladas

| Decisão | Registro |
|---|---|
| Boundary transacional explícito, `SERIALIZABLE` só na ingestão | `docs/adr/0009-operacao-critica-e-concorrencia.md` |
| Retry da operação inteira, com lista fechada do que vale refazer | `docs/adr/0009` |
| **Terceira camada de idempotência muda de forma sob isolamento forte** | `docs/adr/0009` |
| Contenção esgotada é 503 com `Retry-After`, nunca 500 | `docs/adr/0009` |
| `SET LOCAL cpu_tuple_cost` dentro da transação crítica | `docs/adr/0009` |
| Semântica "equivalente a alguma ordem serial", e não "todos enxergam todos" | `docs/adr/0009` |
| Transactional Outbox como modelo local, conteúdo mínimo e versionado | `docs/adr/0009` |
| Evento atrasado não reescreve avaliação histórica | `docs/adr/0009` |

### Defeitos reais encontrados e corrigidos

**1. Conflitos falsos entre clientes que não compartilham nada.** Doze
requisições simultâneas de **doze clientes diferentes** produziam tempestade de
`40001`: 28 retentativas e duas respostas 503.

A causa está na documentação do PostgreSQL, literalmente: *"A sequential scan
will always necessitate a relation-level predicate lock. This can result in an
increased rate of serialization failures."* Com a tabela pequena o planejador
varre sequencialmente, e o bloqueio de predicado deixa de cobrir a faixa de um
cliente para cobrir a tabela inteira.

Corrigido com `SET LOCAL cpu_tuple_cost = 1.0` **dentro da transação crítica** —
a mitigação que a própria documentação recomenda, no escopo mais estreito
possível. Verificado com `EXPLAIN`. O orçamento de retentativas subiu de 4 para
8 com base na medição, e não no palpite.

**2. Contenção esgotada virava 500.** Um integrador com retry automático leria
"algo quebrou, não insista" quando a resposta correta é "estava disputado,
repita". Agora é 503 com `Retry-After` e código `contencao_de_concorrencia` —
seguro por construção, porque repetir com a mesma chave de idempotência ou
devolve a avaliação original ou cria a transação uma vez só.

**3. SQL cru ignora o filtro global de tenant.** Um teste desta fase lia
`eventos_de_saida` com `SqlQuery` e passava sozinho, mas falhava junto dos
outros — enxergava os eventos dos demais tenants. Não é falha de produção hoje,
mas é o alerta certo na hora certa: a Fase 5 vai querer `FOR UPDATE SKIP
LOCKED`, e ali o filtro também não vale.

### Fora de escopo, deliberadamente

- **Sem publicação de eventos.** A Outbox grava e as linhas ficam pendentes. O
  despachante, SQS, Inbox e DLQ são a Fase 5 — que é onde `PublicadoEm` e
  `TentativasDePublicacao`, já criados, passam a ser usados.
- **Sem alerta.** Score alto ainda não gera alerta nem caso. Fases 6 e 7.
- **Sem métrica exportada.** O `Meter` e os dois contadores existem; coleta e
  painel são a Fase 12.
- **Sem mudança de frontend.** A fase é de backend. O 503 já cai na categoria
  "servidor" do cliente HTTP, que oferece "Tentar de novo".

### Débito técnico não bloqueante

1. **CI ainda não executado.** Cada comando do workflow roda localmente e está
   verde, mas o GitHub Actions só roda após o primeiro `push`, que continua não
   autorizado.
2. **Conflito falso encolhe, mas não desaparece, com tabela pequena.** Uma
   organização recém-criada, com poucas transações, ainda pode gastar
   retentativas em uma rajada simultânea. O comportamento é correto em qualquer
   caso — no pior deles, 503 com `Retry-After`.
3. **Baseline medida em uma máquina só, em Debug, sem carga sustentada.** Ver as
   limitações listadas em `docs/baseline-de-performance.md`.
4. **Verificação visual no navegador segue pendente** — extensão do Chrome
   desconectada desde a Fase 2.
5. **Rate limit continua por instância do processo.** Fase 11.

---

# FASE 5 — BACKBONE ASSÍNCRONO: OUTBOX, SQS, INBOX E DLQ

## 5.1 Objetivo

Adicionar efeitos assíncronos confiáveis sem comprometer a decisão síncrona.

Ao final:

> uma avaliação confirmada gera evento persistente, é publicada por SQS e processada por consumidor idempotente.

---

## 5.2 Transactional Outbox

Formalizar tabela/modelo Outbox.

Evento mínimo:

```text
TransactionEvaluated.v1
```

Outbox deve ser gravado na mesma transação da avaliação.

---

## 5.3 Envelope

Implementar contrato contendo:

- EventId;
- EventType;
- Version;
- TenantId;
- CorrelationId;
- OccurredAt;
- payload versionado.

Não usar payload polimórfico sem contrato claro.

---

## 5.4 Dispatcher

Criar dispatcher capaz de:

- buscar pendentes;
- claim/processar lote;
- publicar;
- marcar sucesso;
- tolerar falha parcial;
- reexecutar sem perder mensagem.

Concorrência de dispatchers deve ser segura.

---

## 5.5 SQS

Provisionar abstração/configuração para:

- fila operacional Standard;
- DLQ;
- visibility timeout adequado;
- retry/redrive controlado.

Infra cloud real pode continuar simulada/local até Fase 14.

A fase pode usar emulator/fakes somente onde não escondam comportamento essencial.

Integração contratual com SQS real deve ser validável.

---

## 5.6 Worker

Criar worker para consumir `TransactionEvaluated.v1`.

Nesta fase o efeito pode ser simples e verificável.

Alertas completos entram na Fase 6.

---

## 5.7 Inbox

Persistir EventId processado ou mecanismo equivalente.

Garantir:

```text
mesma mensagem N vezes
→ efeito uma vez
```

---

## 5.8 Partial failures

Quando worker processar batch:

- uma mensagem inválida não deve obrigatoriamente reprocessar todas as bem-sucedidas;
- comportamento deve respeitar suporte da integração SQS/Lambda.

---

## 5.9 Correlation ID

Preservar:

```text
HTTP request
→ evaluation
→ outbox
→ SQS
→ worker
```

Criar teste de propagação.

---

## 5.10 Security Gate 5

Validar:

- mensagem forjada;
- event type desconhecido;
- versão desconhecida;
- tenant inconsistente;
- payload inválido;
- replay;
- secret/configuração fora de log;
- poison message;
- DLQ behavior.

---

## 5.11 Testes de falha obrigatórios

### Falha após commit e antes de publish

Evento permanece recuperável.

### Publish duplicado

Consumidor produz efeito único.

### Worker cai após efeito e antes de ACK

Retry não duplica efeito.

### Outbox dispatcher concorrente

Mesmo item não causa inconsistência.

### Mensagem inválida recorrente

Vai para fluxo de falha/DLQ definido.

---

## 5.12 Critérios de conclusão

- [x] Outbox transacional funcionando.
- [x] Dispatcher idempotente/reexecutável.
- [x] SQS Standard configurada em arquitetura.
- [x] Inbox persistente.
- [x] Worker idempotente.
- [x] Correlation ID preservado.
- [x] DLQ desenhada/testada.
- [x] Falhas críticas simuladas.
- [x] Security Gate 5 verde.
- [ ] CI verde — *pendente do primeiro `push`, que não foi autorizado. Cada passo do workflow foi executado localmente e está verde.*

---

## 5.13 Resultado da Fase 5

**Concluída em 2026-09-04.**

### Evidências

| Critério | Como foi verificado |
|---|---|
| Build backend | `dotnet build -c Release --no-incremental`, **0 erros e 0 avisos** |
| Build frontend | `npm run build`, `oxlint` e `prettier --check` limpos |
| Outbox transacional | evento na mesma transação da avaliação (Fase 4); despacho separado, verificável |
| Dispatcher reexecutável | publica **antes** de marcar; falha parcial não derruba o lote |
| Dispatcher concorrente | 3 despachantes simultâneos, 12 eventos → **cada um publicado uma vez** |
| Contrato SQS Standard | 10 testes descrevem visibilidade, recibo por entrega, contagem, redrive e ausência de ordem — sem citar PostgreSQL |
| Inbox persistente | restrição única `(consumidor, evento)`; efeito e marca no mesmo commit |
| Worker idempotente | 5 replays → **5 repetidos, 0 processados, contador em 1** |
| Correlation ID | 4 elos verificados: cabeçalho HTTP, linha da Outbox, corpo da mensagem e **linha da Inbox** |
| DLQ | corpo, contagem de entregas e motivo preservados; não alcançável por rota HTTP |
| Falhas obrigatórias | as 5 cenários do 5.11, cada um com teste próprio |
| Testes | 316 unitários + 15 arquitetura + 186 integração + 45 frontend = **562, todos verdes** |
| Security Gate 5 | [`docs/security-gate-5.md`](docs/security-gate-5.md) |
| Migration | `MensageriaEProjecoes` aplicada em PostgreSQL real; `has-pending-model-changes` sem alteração pendente |
| Dependências | `npm audit`: 0; nenhum pacote novo no backend |
| Segredos | `gitleaks detect`: nenhum |

### As cinco falhas obrigatórias (ROADMAP 5.11)

| Cenário | Resultado |
|---|---|
| **Falha após commit e antes do publish** | evento fica pendente; um processo novo o encontra e publica |
| **Publish duplicado** | 2 mensagens, mesmo `eventId` → 1 processado, 1 repetido, contador em 1 |
| **Worker cai após efeito e antes do ACK** | reentrega reconhecida pela Inbox; contador não muda |
| **Dispatcher concorrente** | `SKIP LOCKED`: cada evento publicado uma vez, zero pendentes |
| **Mensagem inválida recorrente** | 5 entregas sem confirmação → fila de mortas, com o corpo guardado |

### Decisões congeladas

| Decisão | Registro |
|---|---|
| Fila em PostgreSQL reproduzindo o contrato do SQS Standard até a Fase 14 | `docs/adr/0010-backbone-assincrono.md` |
| Publicar antes de marcar: duplicata é aceitável, perda não é | `docs/adr/0010` |
| Envelope com tipo e versão **separados**, fora do payload | `docs/adr/0010` |
| Inbox decide pela restrição única, no mesmo commit do efeito | `docs/adr/0010` |
| Efeito é um **contador**, porque é o pior caso para duplicata | `docs/adr/0010` |
| O que chega da fila é dado: tenant conferido contra o banco | `docs/adr/0010` |
| Não se apaga o que não se entende — o redrive decide | `docs/adr/0010` |
| Laços de fundo desligados por padrão, ligados em Development | `docs/adr/0010` |
| Duas filas desde já: operacional e backtests | `docs/adr/0010` |

### Defeitos reais encontrados e corrigidos

**1. SQL cru NÃO escapa do filtro global de tenant.** O despachante usava
`FromSql` para o `FOR UPDATE SKIP LOCKED` e publicava **zero eventos** — sem
erro nenhum no caminho. O EF Core compõe o filtro **por cima** do `FromSql`,
como se fosse uma subconsulta; como o despachante roda sem identidade, o tenant
efetivo é vazio e a consulta devolvia zero linha.

A Fase 4 tinha registrado a suspeita ao encontrar o inverso em um teste. Aqui
ela apareceu no caminho principal. Corrigido com `IgnoreQueryFilters` pelo
**nome** do filtro.

**2. A fila guardava o corpo como `jsonb`, e isso a tornava mais rígida do que
o SQS.** Um corpo corrompido era recusado **pelo banco**, e não pelo consumidor
— o caminho de mensagem envenenada nunca era exercitado. E um corpo válido
voltava com as chaves reordenadas, escondendo qualquer teste sobre o formato de
fio exato. Para o SQS, o corpo é uma cadeia de bytes opaca. Corrigido para
`text`.

**3. Isolamento entre classes de teste.** O despachante lê a Outbox de todos os
tenants — é inerente ao desenho. As outras classes ingerem e nunca despacham,
deixando eventos pendentes; um teste de mensageria publicava o acúmulo delas
junto com o próprio evento. Passava isolado e falhava na suíte. Corrigido com
uma limpeza explícita do estado global no preparo, com o motivo documentado.

### Fora de escopo, deliberadamente

- **Sem SQS real.** ROADMAP 5.5 permite infra simulada até a Fase 14. O
  contrato está testado de forma independente da implementação: lá é adaptador,
  não reescrita.
- **Sem alerta.** O efeito desta fase é uma projeção de contagem. Alertas são a
  Fase 6, e é o mesmo evento que vai alimentá-los.
- **Sem tela.** A projeção diária não é exposta por nenhuma rota. Fase 10.
- **Sem métrica exportada.** Profundidade de fila e idade da mensagem mais
  antiga são Fase 12.

### Débito técnico não bloqueante

1. **CI ainda não executado.** Cada comando roda localmente e está verde; o
   GitHub Actions só roda após o primeiro `push`, que continua não autorizado.
2. **Sem ferramenta de reprocessamento da DLQ.** A estratégia existe
   (inspecionar, corrigir a causa, reenviar); a ferramenta é a Fase 12. Hoje a
   inspeção é por acesso administrativo ao banco.
3. **A fila divide o banco com os dados.** Em volume de portfólio não é
   problema; a Fase 14 separa.
4. **O efeito aparece com atraso de até um intervalo de laço** (2 s ociosos).
   Esperado para um caminho assíncrono, mas registrado para que ninguém trate a
   projeção como leitura imediata.
5. **Verificação visual no navegador segue pendente** — extensão do Chrome
   desconectada desde a Fase 2. Nenhuma tela nova nesta fase.

---

# FASE 6 — ALERTAS OPERACIONAIS

## 6.1 Objetivo

Transformar avaliações relevantes em uma fila operacional para analistas.

Ao final:

> transações com decisão/sinais configurados geram alertas idempotentes consultáveis e priorizáveis.

---

## 6.2 Regra operacional inicial

Definir política simples.

Sugestão:

- `Permitir` → normalmente não gera alerta;
- `Revisar` → gera alerta;
- `Bloquear` → gera alerta de maior prioridade.

A política deve ser configurável somente no nível necessário.

Não criar engine secundário complexo.

---

## 6.3 Alert entity

Alert deve registrar:

- tenant;
- avaliação;
- transação;
- severidade/prioridade;
- status;
- horário;
- sinais resumidos ou referência;
- origem do evento.

Constraint deve impedir duplicidade por avaliação/política quando apropriado.

---

## 6.4 Processamento

`TransactionEvaluated.v1`

```text
↓
Alert handler
↓
criar alerta quando aplicável
```

Consumidor deve continuar idempotente.

---

## 6.5 Fila no frontend

Criar tela **Alertas** com:

- filtros;
- decisão;
- score;
- horário;
- idade do alerta;
- principais sinais;
- prioridade;
- paginação;
- ordenação.

Evitar cards decorativos.

---

## 6.6 Detalhe

A partir do alerta, analista deve navegar para:

- transação;
- avaliação;
- sinais.

Caso ainda não existe nesta fase.

---

## 6.7 Security Gate 6

Validar:

- alerta cross-tenant;
- filtros manipulados;
- bulk operations inexistentes/limitadas;
- status interno protegido;
- evento duplicado não duplica alerta;
- role de Auditor não altera alerta;
- Analista não altera regra.

---

## 6.8 Critérios de conclusão

- [x] Revisar/Bloquear geram alerta segundo política.
- [x] Evento duplicado não duplica alerta.
- [x] Lista operacional funcional.
- [x] Filtros server-side.
- [x] Navegação alerta → transação.
- [x] Isolamento de tenant testado.
- [x] Security Gate 6 verde.
- [ ] CI verde — *pendente do primeiro `push`, que não foi autorizado. Cada passo do workflow foi executado localmente e está verde.*

---

## 6.9 Resultado da Fase 6

**Concluída em 2026-09-05.**

### Evidências

| Critério | Como foi verificado |
|---|---|
| Build backend | `dotnet build -c Release`, **0 erros e 0 avisos** |
| Build frontend | `npm run build`, `oxlint` e `prettier --check` limpos |
| Política | `Permitir` → nenhum alerta; `Revisar` → Média; `Bloquear` → Alta, com a versão da política gravada em cada alerta |
| **Evento duplicado** | duas camadas, testadas **uma de cada vez**: com a Inbox, `0 processados / 1 repetido`; sem a marca da Inbox, a restrição `alertas.avaliacao_id` devolve o **mesmo** alerta |
| Savepoint por efeito | marca de um consumidor apagada → o alerta volta e o contador **não** soma de novo |
| Lista operacional | filtros de prioridade, decisão, score mínimo e período; ordenação por chegada, score e prioridade; paginação com total real |
| Filtros no servidor | 11 formas forjadas, todas `400`; nenhuma devolve a fila sem filtro |
| Navegação | cada linha carrega `transacaoId`; a tela da Fase 3 mostra avaliação e sinais completos |
| Isolamento | dois tenants com alerta cada; a separação é conferida **no banco**, e não só pela resposta HTTP |
| Verificação em app real | app no ar em `localhost:5174` contra banco próprio: ingestão → Outbox → fila → worker → **2 alertas** (`Alta 75` e `Media 45`) com os laços de fundo ligados |
| Testes | 352 unitários + 15 arquitetura + 224 integração + 61 frontend = **652, todos verdes** |
| Security Gate 6 | [`docs/security-gate-6.md`](docs/security-gate-6.md) |
| Migration | `AlertasOperacionais` aplicada em PostgreSQL real; `has-pending-model-changes` sem alteração pendente |
| Dependências | `dotnet list package --vulnerable`: nenhuma; `npm audit`: 0 |
| Segredos | `gitleaks detect` e `detect --no-git`: nenhum |

### Decisões congeladas

| Decisão | Registro |
|---|---|
| Um consumidor a mais, e não uma fila a mais: fan-out dentro da transação | `docs/adr/0011-alertas-operacionais.md` |
| Um savepoint por efeito, porque no PostgreSQL um erro aborta a transação inteira | `docs/adr/0011` |
| Duas camadas de idempotência, cada uma testada com a outra derrubada | `docs/adr/0011` |
| Política é código versionado, e não um segundo motor de risco | `docs/adr/0011` |
| O alerta copia o que a fila filtra e referencia os sinais | `docs/adr/0011` |
| Fila somente leitura: nenhuma rota altera alerta nesta fase | `docs/adr/0011` |
| Filtro fora do vocabulário é recusado, nunca ignorado | `docs/adr/0011` |
| Ordenação por prioridade não usa a coluna de texto | `docs/adr/0011` |

### Defeito real encontrado e corrigido

**A ordenação por prioridade estava alfabética, e o alfabeto é o inverso da
gravidade.** A coluna guarda texto — `"Alta"`, `"Media"` — para que uma consulta
manual durante uma investigação seja legível. Mas `"Alta"` vem antes de
`"Media"` no alfabeto, então pedir "mais grave primeiro" trazia os **menos
graves** no topo.

O que torna o defeito perigoso é que **nada na tela o denunciaria**: a lista
pareceria perfeitamente ordenada, e o analista trabalharia a fila na ordem
errada sem nunca perceber. Corrigido com uma expressão explícita de gravidade
na consulta, mantendo a coluna legível.

Encontrado porque o teste afirma a ordem esperada — `[75, 45]` — em vez de
afirmar apenas que a lista veio ordenada.

### Fora de escopo, deliberadamente

- **Sem ação humana sobre o alerta.** Não há rota que crie, altere, atribua,
  feche ou apague — e o Security Gate 6 verifica a ausência com sete métodos e
  caminhos. Assumir e resolver pertencem ao **caso**, que é a Fase 7.
- **Sem tela de detalhe do alerta.** A navegação vai para a transação, onde a
  explicação completa já mora desde a Fase 3.
- **`StatusDoAlerta` com um valor só.** Estado novo só com necessidade
  operacional clara (`CLAUDE.md` seção 12), e ela nasce com o caso.
- **Sem métrica exportada.** O contador `alertas_criados` existe, com dimensão
  de prioridade; coleta e painel são a Fase 12.

### Débito técnico não bloqueante

1. **CI ainda não executado.** Cada comando roda localmente e está verde; o
   GitHub Actions só roda após o primeiro `push`, que continua não autorizado.
2. **Verificação visual da tela de Alertas no navegador.** A extensão do Chrome
   voltou a responder e a aplicação foi conferida no ar — a tela de login
   renderiza e a API devolve os dois alertas corretos. O restante do percurso
   exigiria digitar a senha de sessão no formulário, o que o agente não faz.
   Débito herdado da Fase 2, agora reduzido a esse último passo.
3. **A ordenação por prioridade depende de uma expressão mantida à mão.**
   Acrescentar um nível novo exige alterá-la, e o compilador não avisa — só o
   teste de integração.
4. **Sem métrica de tamanho da fila nem de idade do alerta mais antigo.**
   Fase 12.
5. **Alerta não expira e não é apagado.** Política de retenção não é assunto da
   v1, mas fica registrado que a tabela cresce sem teto.

---

# FASE 7 — CASOS E INVESTIGAÇÃO

## 7.1 Objetivo

Criar a experiência central do analista humano.

Ao final:

> alertas podem ser reunidos em casos, atribuídos, investigados e resolvidos com trilha completa.

---

## 7.2 Caso

Implementar:

- Case;
- status;
- responsável;
- timestamps;
- resultado;
- associações de alertas;
- associações de transações derivadas;
- timeline.

---

## 7.3 Criação

Na v1:

> caso pode ser criado por Analista/Supervisor a partir de um ou mais alertas compatíveis do mesmo tenant.

Não implementar auto-grouping inteligente nesta fase.

Evitar complexidade de graph/entity resolution.

---

## 7.4 Workflow

Estados:

```text
Novo
→ EmAnalise
→ Resolvido
```

Transições devem ser validadas no backend.

Resultado obrigatório ao resolver:

- FraudeConfirmada;
- Legitima;
- Inconclusiva.

---

## 7.5 Ownership

Definir regras claras para:

- assumir caso;
- transferir caso;
- resolver;
- adicionar nota.

Evitar locking pessimista de edição se não houver problema real.

Usar concorrência otimista/versionamento se necessário para impedir lost update.

---

## 7.6 Timeline

Registrar:

- criação;
- associação de alertas;
- responsável;
- mudança de status;
- nota;
- resolução.

Eventos operacionais relevantes são append-only.

---

## 7.7 Notas

Notas de investigação:

- autoria;
- timestamp;
- tenant;
- conteúdo sanitizado/validado;
- sem HTML arbitrário.

Definir política de edição.

Preferência inicial:

> notas publicadas não são silenciosamente sobrescritas; correção relevante gera nova entrada ou mecanismo auditável.

---

## 7.8 Frontend — Workspace do caso

Tela deve reunir:

- cabeçalho;
- risco;
- responsável;
- status;
- resultado;
- principais sinais;
- transações;
- alertas;
- histórico recente;
- timeline;
- notas;
- ações permitidas.

Essa tela é prioridade visual alta.

---

## 7.9 Feedback operacional

Ao resolver caso, associar resultado às transações relevantes de forma explícita.

Esse feedback alimentará métricas e backtests posteriores.

Não treinar modelo de IA automaticamente.

---

## 7.10 Security Gate 7

Testar:

- resolução por usuário sem perfil;
- caso cross-tenant;
- alerta de tenant B associado a caso A;
- mass assignment de status;
- atribuição para usuário de outro tenant;
- XSS em nota;
- lost update;
- alteração de caso resolvido fora da regra;
- Auditor read-only.

---

## 7.11 Critérios de conclusão

- [x] Caso criado a partir de alerta(s).
- [x] Status validado.
- [x] Ownership funcionando.
- [x] Timeline append-only.
- [x] Resultado obrigatório na resolução.
- [x] Fraude/Legítima/Inconclusiva persistidos.
- [x] Workspace de investigação utilizável.
- [x] Falso positivo suportado corretamente.
- [x] Security Gate 7 verde.
- [ ] CI verde — *pendente do primeiro `push`, que não foi autorizado. Cada passo do workflow foi executado localmente e está verde.*

---

## 7.12 Resultado da Fase 7

**Concluída em 2026-09-05.**

### Evidências

| Critério | Como foi verificado |
|---|---|
| Build backend | `dotnet build -c Release --no-incremental`, **0 erros e 0 avisos** |
| Build frontend | `npm run build`, `oxlint` e `prettier --check` limpos |
| Caso a partir de alertas | domínio recusa lista vazia; abertura leva os alertas para o caso na mesma operação |
| Transições validadas | `Novo → EmAnalise` por consequência de assumir; resolver exige `EmAnalise` **com** responsável |
| Ownership | assumir é para si; transferir é ato de supervisão, com destino conferido (mesmo tenant, ativo, não Auditor) |
| Timeline append-only | sem método de alteração, sem rota, e com **sequência** por caso — horário não ordena porque abrir grava dois eventos no mesmo instante |
| Resultado obrigatório | vocabulário fechado; 5 valores inválidos, todos `400`; nunca cai num padrão |
| **Falso positivo** | ponta a ponta: motor decide `Revisar`, pessoa conclui `Legitima`, e a decisão automática **não** é reescrita |
| Feedback operacional | um veredito por **transação**, com restrição única — é o que a Fase 9 vai ler como verdade apurada |
| **Lost update** | sequencial (`409`) e **simultâneo**: duas resoluções em paralelo → exatamente 1× `200`, 1× `409`, 1 resultado, 1 veredito |
| Workspace | risco, transações, sinais, timeline, notas e ações numa tela só; ações vêm do servidor |
| Isolamento | caso e alerta de outro tenant respondem `404`; o alerta do outro tenant fica intacto, conferido no banco |
| Testes | 386 unitários + 15 arquitetura + 260 integração + 73 frontend = **734, todos verdes** |
| Security Gate 7 | [`docs/security-gate-7.md`](docs/security-gate-7.md) |
| Migration | `CasosEInvestigacao` aplicada em PostgreSQL real; `has-pending-model-changes` sem alteração pendente |
| Dependências | `dotnet list package --vulnerable`: nenhuma; `npm audit`: 0 |
| Segredos | `gitleaks detect` e `detect --no-git`: nenhum |

### Decisões congeladas

| Decisão | Registro |
|---|---|
| Um caso nasce de alertas, e um alerta pertence a no máximo um caso | `docs/adr/0012-casos-e-investigacao.md` |
| Estado e timeline mudam na mesma operação de domínio | `docs/adr/0012` |
| Resolvido é imutável — corrigir exige caso novo | `docs/adr/0012` |
| Duas camadas contra lost update: versão do cliente e token no banco | `docs/adr/0012` |
| Recusar texto com marcação, nunca limpar em silêncio | `docs/adr/0012` |
| Assumir é para si; transferir é supervisão | `docs/adr/0012` |
| `acoesPermitidas` vêm do servidor, e o servidor recusa de novo | `docs/adr/0012` |
| Rotas de ação em vez de `PUT` no recurso inteiro | `docs/adr/0012` |
| Timeline e notas não são navegação do agregado | `docs/adr/0012` |
| Timeline ordenada por sequência, e não por horário | `docs/adr/0012` |
| Veredito por transação, com restrição única | `docs/adr/0012` |

### Defeitos reais encontrados e corrigidos

**1. O EF Core trata filho novo com chave preenchida como `UPDATE`.** Os
identificadores da timeline são gerados pelo domínio (UUIDv7). Quando o EF
encontrava uma entrada nova dentro de uma coleção de navegação, concluía que a
linha já existia e emitia `UPDATE` — que atingia zero linhas e virava um
**falso conflito de concorrência**. Toda nota falhava com `409`.

O sintoma enganava: a mensagem dizia "o recurso mudou depois que você o abriu",
exatamente o que se esperaria de um lost update legítimo. Corrigido tirando
timeline e notas da navegação — o repositório grava as entradas novas
explicitamente, e o agregado só acumula o que a operação produziu.

**2. Horário não ordena uma trilha.** Abrir um caso grava dois eventos no mesmo
instante, e o UUIDv7 não desempata porque sorteia os bits finais. A timeline
mostrava "alerta associado" antes de "caso aberto" de vez em quando. Corrigido
com uma sequência por caso, com índice único — uma história que muda de ordem
entre duas leituras deixa de ser prova.

**3. A corrida perdida virava `500`.** Na resolução simultânea, quem perde pode
esbarrar no token de versão **ou** na restrição única do veredito, dependendo
de quem chegou onde primeiro. O segundo caminho não era traduzido, e o cliente
recebia "algo quebrou" quando a resposta certa é "outra pessoa concluiu antes;
recarregue". **Só a suíte completa pegou** — isolado, o teste passava.

**4. `Max` sobre enum gravado como texto ordena pelo alfabeto.** O resumo de
alertas por caso usaria `Max(prioridade)`, e `"Media"` venceria `"Alta"` — a
lista mostraria "Média" num caso que contém um bloqueio. É a mesma armadilha
que a Fase 6 encontrou na ordenação da fila, e aqui foi evitada antes de virar
defeito, com uma expressão explícita de gravidade.

### Fora de escopo, deliberadamente

- **Sem reabertura de caso.** Corrigir uma conclusão exige um caso novo, que a
  timeline registra. O motivo é a Fase 9: um veredito que muda depois faria
  backtests antigos passarem a mentir.
- **Sem auto-agrupamento de alertas.** O ROADMAP 7.3 é explícito: nada de
  entity resolution nem grafo nesta fase.
- **Sem transferência pela tela.** A rota existe e é testada; o workspace ainda
  só oferece assumir e resolver.
- **Sem busca por texto** em casos e notas. Fase 10.
- **Sem consulta de auditoria.** A trilha das ações de caso é gravada e
  testada; a tela é a Fase 10.

### Débito técnico não bloqueante

1. **CI ainda não executado.** Cada comando roda localmente e está verde; o
   GitHub Actions só roda após o primeiro `push`, que continua não autorizado.
2. **A validação de marcação é uma heurística.** Uma nota legítima que contenha
   `<b` será recusada. A alternativa — limpar em silêncio — foi julgada pior
   numa investigação, mas o atrito existe.
3. **A tela de casos não pagina.** A API pagina e devolve o total; a interface
   ainda mostra só a primeira página.
4. **Sem transferência no workspace**, como registrado acima.
5. **Verificação visual no navegador segue pendente.** O último passo exige
   digitar a senha de sessão no formulário, o que o agente não faz. Débito
   herdado da Fase 2 e reduzido a esse passo na Fase 6.
6. **Casos, notas e vereditos não expiram.** As tabelas crescem sem teto.

---

# FASE 8 — GESTÃO E VERSIONAMENTO DE REGRAS

## 8.1 Objetivo

Permitir que Supervisor administre regras sem destruir explicabilidade histórica.

Ao final:

> regra pode nascer como rascunho, ser validada, publicada como versão imutável e futuramente substituída.

---

## 8.2 Administração

Criar telas/API para:

- listar regras;
- criar regra;
- criar draft;
- editar draft;
- validar configuração;
- visualizar versões;
- publicar;
- substituir/desativar conforme semântica.

---

## 8.3 Tipos fechados

Frontend deve montar forms a partir de contratos conhecidos.

Não aceitar:

- código;
- SQL;
- script;
- expressão arbitrária.

---

## 8.4 Publicação

Ao publicar:

- validar configuração;
- criar versão imutável;
- registrar auditoria;
- preservar versão anterior;
- atualizar profile version conforme desenho.

Nenhum update destrutivo na versão publicada.

---

## 8.5 Perfil de risco

Permitir gerenciar:

- thresholds;
- regras ativas;
- ordem se semanticamente relevante;
- pesos;
- versão.

Publicação cria snapshot imutável.

---

## 8.6 Concorrência administrativa

Dois Supervisores editando/publicando simultaneamente não podem sobrescrever silenciosamente.

Usar version token/concurrency control apropriado.

---

## 8.7 Auditoria

Registrar:

- criação;
- mudança de draft;
- publicação;
- substituição;
- alteração de perfil;
- usuário responsável.

---

## 8.8 Security Gate 8

Testar:

- Analista publicando regra;
- Auditor editando;
- tenant B acessando rule A;
- edição de versão publicada;
- config inválida;
- tipo de regra desconhecido;
- payload com campo arbitrário;
- concorrência de publicação.

---

## 8.9 Testes históricos

Obrigatório:

```text
Rule v1
→ avaliação A
→ publicar v2
→ abrir A
```

Resultado:

> A continua explicada pela v1.

---

## 8.10 Critérios de conclusão

- [x] Draft editável.
- [x] Publicação imutável.
- [x] Rule versions navegáveis.
- [x] Risk profile versionado.
- [x] Concurrency control administrativo.
- [x] Auditoria de publicação.
- [x] Histórico antigo preservado.
- [x] UI de regras utilizável.
- [x] Security Gate 8 verde.
- [ ] CI verde — *pendente do primeiro `push`, que não foi autorizado. Cada passo do workflow foi executado localmente e está verde.*

---

## 8.11 Resultado da Fase 8

**Concluída em 2026-09-06.**

### Evidências

| Critério | Como foi verificado |
|---|---|
| Build backend | `dotnet build -c Release`, **0 erros e 0 avisos** |
| Build frontend | `tsc --noEmit`, `oxlint` e `prettier --check` limpos |
| Draft editável | regra nasce como rascunho; salvar, descartar e publicar são operações próprias, e o rascunho não alcança o motor |
| Publicação imutável | `VersaoDeRegra` e `VersaoDePerfilDeRisco` sem método de alteração, sem setter público e sem rota — verificado por teste de arquitetura |
| Versões navegáveis | histórico completo por regra, da mais recente para a mais antiga, na tela e na API |
| Perfil versionado | toda mudança que alcança o motor publica uma versão nova de perfil, na mesma transação |
| Concorrência administrativa | token de versão na regra, token de versão do perfil e índices únicos `(regra, número)` e `(perfil, número)` |
| Auditoria | rascunho salvo, rascunho descartado, versão publicada, ativação e **toda sucessão de perfil**, com autor |
| **Histórico preservado** | v1 avalia → v2 é publicada → a avaliação antiga continua com score, pontos, explicação e versão de perfil originais |
| Catálogo fechado | configuração entra como números nomeados; campo desconhecido, campo faltando e valor fora da faixa são `400` |
| UI | catálogo em leitura para todos; rascunho, publicação, ativação e histórico para a supervisão |
| Testes | 432 unitários + 22 arquitetura + 305 integração + 91 frontend = **850, todos verdes** |
| Security Gate 8 | [`docs/security-gate-8.md`](docs/security-gate-8.md) |
| Migration | `GestaoDeRegras` aplicada em PostgreSQL real; `has-pending-model-changes` sem alteração pendente |
| Dependências | `dotnet list package --vulnerable`: nenhuma; `npm audit`: 0 |
| Segredos | `gitleaks detect` e `detect --no-git`: nenhum |

### Decisões congeladas

| Decisão | Registro |
|---|---|
| A regra é mutável; a versão publicada, nunca | `docs/adr/0013-gestao-e-versionamento-de-regras.md` |
| Nada muda o motor a não ser publicar uma versão de perfil | `docs/adr/0013` |
| Um perfil publicado precisa de ao menos uma regra | `docs/adr/0013` |
| Duas regras do mesmo tipo são permitidas; o nome é único por organização | `docs/adr/0013` |
| Configurar é escolher números nomeados, nunca escrever lógica | `docs/adr/0013` |
| O nome é rótulo da identidade e não entra na versão | `docs/adr/0013` |
| Publicar rascunho igual à versão em vigor é recusado | `docs/adr/0013` |
| Concorrência administrativa em duas camadas, mais os índices de numeração | `docs/adr/0013` |
| Rotas de ação em vez de `PUT` no recurso | `docs/adr/0013` |
| O rascunho mora na regra, sem tabela própria | `docs/adr/0013` |

### Defeitos reais encontrados e corrigidos

**1. A sucessão do perfil não deixava rastro próprio.** A trilha registrava a
publicação na regra, mas a versão nova de perfil só era auditada quando alguém
mexia nos limiares. Quem perguntasse "quando o perfil em vigor mudou?" não veria
as trocas causadas por publicação ou desativação de regra — que são a maioria
delas. Corrigido movendo a auditoria para dentro da própria sucessão, com o
motivo em cada registro.

**2. `OrderBy(Tipo)` deixou de ser uma ordem total.** Permitir duas regras do
mesmo tipo tornou instável a ordem de execução do motor e a ordem dos sinais
exibidos. O score não mudaria — soma não depende de ordem —, mas a promessa de
determinismo do ADR 0008 sim: duas leituras da mesma avaliação poderiam trazer
os sinais em ordens diferentes. Corrigido com `ThenBy(RegraId)` nos quatro
pontos que ordenam sinais ou regras.

**3. Salvar o rascunho sem tocar em nada enviava configuração vazia.** No editor
da tela da regra, o estado local dos campos nascia vazio e só era preenchido
depois que o contrato do tipo chegava do servidor. Quem abrisse a regra e
clicasse em "Salvar rascunho" mandaria `{}` e receberia `400` por campo
faltando, sem entender por quê.

**4. A migration gerada pelo scaffold desativaria todas as regras
existentes.** O EF sugeriu `ativa` com `DEFAULT FALSE`. Aplicada assim, toda
organização já provisionada ficaria com quatro regras desligadas, e a primeira
publicação de perfil seria recusada por não haver regra nenhuma. Reescrita com
colunas anuláveis, `UPDATE` de preenchimento e só então `NOT NULL`.

**5. O primeiro teste de concorrência não testava concorrência.** Duas
publicações de regras diferentes disparadas com `Task.WhenAll` sobre o mesmo
`HttpClient` executavam em sequência: as duas passavam, e a asserção "1 criado,
1 conflito" falhava por um motivo que não era defeito do produto. Reescrito em
dois testes — um determinístico, sobre a mesma regra, e um em paralelo real,
afirmando o invariante em vez da divisão de respostas.

### Fora de escopo, deliberadamente

- **Sem backtest antes de publicar.** O `CLAUDE.md` seção 23 coloca o backtest
  entre rascunho e publicação, e ele é a Fase 9. Até lá, o rascunho é a única
  etapa de revisão.
- **Sem exclusão de regra.** Desativar é o caminho: excluir apagaria a
  referência das versões que explicam avaliações antigas.
- **Sem aprovação de dois supervisores.** Publicar é ato de uma pessoa só; um
  segundo par de olhos seria decisão de produto, e não está no ROADMAP.
- **Sem comparação lado a lado entre duas versões** na tela. O histórico mostra
  cada uma; a diferença fica por conta de quem lê.
- **Sem tipo de regra novo.** O catálogo continua com os quatro da Fase 3 — o
  que mudou é quem pode configurá-los.

### Débito técnico não bloqueante

1. **CI não executado.** Continua dependendo do primeiro `push`, que não foi
   autorizado. Cada comando do workflow foi rodado localmente e está verde.
2. **Uma regra desativada e reativada gera duas versões de perfil.** É ruído no
   histórico do perfil, e é o preço de ter uma única forma de mudar o motor.
3. **A tela de regras não pagina.** O catálogo tem quatro regras hoje; a API
   também não pagina esta lista, e o teto natural é o número de tipos vezes o
   número de configurações que a operação queira manter.
4. **Versões nunca expiram.** Nem devem, porque explicam avaliações antigas —
   mas fica registrado que `versoes_de_regra` e `versoes_de_perfil_de_risco`
   crescem sem teto.
5. **`Down` da migration não é reversível** depois que existir uma segunda regra
   de um mesmo tipo na mesma organização: o índice único antigo recusaria. Está
   documentado no próprio arquivo da migration.

---

# FASE 9 — BACKTESTS

## 9.1 Objetivo

Permitir avaliar impacto histórico de uma regra/perfil candidato antes de publicação.

Ao final:

> Supervisor executa backtest assíncrono, acompanha execução e visualiza resultados sem alterar produção.

---

## 9.2 Modelo

Implementar:

- BacktestRun;
- status;
- parâmetros;
- snapshot da regra/perfil candidato;
- janela histórica;
- contagem;
- resultados agregados;
- erros;
- timestamps.

---

## 9.3 Assíncrono

Backtest deve entrar na fila dedicada.

```text
request
 ↓
persist BacktestRun
 ↓
enqueue
 ↓
worker
 ↓
mesmo RiskEngine
 ↓
resultado
```

Não executar grande backtest dentro da request HTTP.

---

## 9.4 Mesmo motor

Não criar `BacktestRiskEngine`.

Usar o mesmo código de avaliação.

Pode haver providers/adapters diferentes para contexto e persistência de resultado, mas sem duplicar semântica de regra.

---

## 9.5 Isolamento de produção

Backtest:

- não altera RiskEvaluation;
- não cria Alert;
- não cria Case;
- não modifica Transaction;
- não publica evento operacional real.

---

## 9.6 Resultado

Apresentar:

- total analisado;
- total que acionaria;
- distribuição de score/decisão;
- fraude confirmada conhecida;
- legítima conhecida;
- inconclusiva;
- sem resultado conhecido.

Não inventar precisão/recall se o conjunto não suportar cálculo correto.

Se calcular métricas estatísticas, nomear e explicar denominadores corretamente.

---

## 9.7 Cancelamento/limites

Definir:

- intervalo máximo;
- quantidade máxima de transações;
- timeout;
- cancelamento seguro se necessário;
- concorrência máxima.

Evitar custo/DoS.

---

## 9.8 Frontend

Tela Backtest:

- criar;
- acompanhar;
- concluído/falhou;
- comparar regra atual vs candidata quando possível;
- visualizar impacto.

---

## 9.9 Security Gate 9

Testar:

- backtest de outro tenant;
- intervalo abusivo;
- spam de execuções;
- payload malicioso;
- worker duplicado;
- reprocessamento;
- cancelamento;
- regra candidata inválida;
- backtest criando efeito operacional indevido.

---

## 9.10 Critérios de conclusão

- [x] Backtest assíncrono.
- [x] Fila separada.
- [x] Mesmo RiskEngine.
- [x] Produção não alterada.
- [x] Resultados explicáveis.
- [x] Limites de abuso.
- [x] UI funcional.
- [x] Execução duplicada tratada conforme contrato.
- [x] Security Gate 9 verde.
- [ ] CI verde — *pendente do primeiro `push`, que não foi autorizado. Cada passo do workflow foi executado localmente e está verde.*

---

## 9.11 Resultado da Fase 9

**Concluída em 2026-09-07.**

### Evidências

| Critério | Como foi verificado |
|---|---|
| Build backend | `dotnet build -c Release`, **0 erros e 0 avisos** |
| Build frontend | `npm run build`, `tsc --noEmit`, `oxlint` e `prettier --check` limpos |
| Backtest assíncrono | `POST` responde `202` e congela; o trabalho acontece no worker, disparado pela Outbox na mesma transação do pedido |
| Fila separada | o despachante roteia por tipo de evento; o consumidor operacional roda e **não** encosta na mensagem de backtest |
| **Mesmo motor** | candidato igual ao perfil vigente reproduz, decisão a decisão, as avaliações gravadas na ingestão — e um teste de arquitetura recusa um segundo motor |
| **Produção não alterada** | 8 contagens conferidas no banco antes e depois; a Outbox ganha exatamente `+1` evento de gatilho e **zero** eventos operacionais |
| Resultados explicáveis | decisões lado a lado, transições com direção, cruzamento com o veredito humano **com denominador**, faixas de score |
| Limites de abuso | janela, volume analisado, volume de contexto, execuções simultâneas por organização e tempo máximo |
| UI | lista que repergunta só enquanto há execução em andamento, formulário que só oferece rascunhos, detalhe com o candidato congelado |
| Execução duplicada | duas mensagens e dois workers em paralelo → **uma** conclusão; reentrega encontra `Concluida` e não reescreve |
| Testes | 490 unitários + 25 arquitetura + 349 integração + 109 frontend = **973, todos verdes** |
| Security Gate 9 | [`docs/security-gate-9.md`](docs/security-gate-9.md) |
| Migration | `Backtests` aplicada em PostgreSQL real; `has-pending-model-changes` sem alteração pendente |
| Dependências | `dotnet list package --vulnerable`: nenhuma; `npm audit`: 0 |
| Segredos | `gitleaks detect` e `detect --no-git`: nenhum |

### Decisões congeladas

| Decisão | Registro |
|---|---|
| O candidato é um **perfil**, e não uma regra solta | `docs/adr/0014-backtests.md` |
| Não existe um segundo motor: fábricas `ParaSimulacao` em memória | `docs/adr/0014` |
| Comparar candidato contra vigente, e não contra o passado gravado | `docs/adr/0014` |
| Tudo o que decide o resultado é congelado no pedido | `docs/adr/0014` |
| Contagens com denominador, nunca precisão nem recall | `docs/adr/0014` |
| Assíncrono, em fila separada, pela Outbox | `docs/adr/0014` |
| O status da execução faz o papel da Inbox | `docs/adr/0014` |
| Cancelar é subir o token de versão, e não mandar um sinal | `docs/adr/0014` |
| Recusar, nunca truncar | `docs/adr/0014` |
| O contexto histórico é lido uma vez, com recuo antes da janela | `docs/adr/0014` |

### Defeitos reais encontrados e corrigidos

**1. O teste de isolamento acusava a própria Outbox.** A primeira versão de
`O_backtest_nao_escreve_nada_em_producao` afirmava "nenhuma contagem muda", e a
Outbox mudava — o pedido grava o evento que dispara o próprio trabalho.

O teste estava errado, mas a pergunta que ele fez estava certa e a resposta
ficou melhor: em vez de tolerar `+1` em silêncio, o teste passou a contar
**por tipo** e a exigir `+1` de `BacktestSolicitado.v1` e **zero** de
`TransacaoAvaliada.v1`. A afirmação deixou de ser "quase nada muda" e virou
"nenhum evento operacional é criado" — que é a invariante que importa, porque
um evento operacional a mais viraria alerta no ciclo seguinte.

**2. Um `Dictionary` com chave de enum anulável não compila.** A apuração
cruza o veredito humano com as decisões, e "sem investigação" é uma das quatro
linhas — o que faz a chave ser `ResultadoDaInvestigacao?`, que não satisfaz a
restrição `notnull`.

Trocado por um vetor de quatro posições em ordem fixa. Ficou melhor do que o
dicionário original: a ordem de leitura deixou de depender de uma ordenação
posterior, e "sem investigação" — que costuma ser a maioria e não deve
encabeçar a leitura — passou a ser sempre a última linha por construção.

### Fora de escopo, deliberadamente

- **Sem comparação entre duas execuções** na tela. Cada uma é lida por si; a
  diferença fica por conta de quem lê.
- **Sem perfil candidato escrito à mão.** O candidato sempre nasce do rascunho
  de uma regra ou de um ajuste de limiares — nunca de configuração enviada pelo
  cliente, que seria uma segunda porta para o motor.
- **Sem auditoria de conclusão e falha.** A trilha registra as ações humanas —
  solicitar e cancelar. Conclusão e falha acontecem num worker, sem autor, e um
  registro de auditoria sem autor responde "o quê" sem responder "quem".
- **Sem métrica exportada.** Duração de backtest e profundidade da fila
  dedicada são Fase 12.

### Débito técnico não bloqueante

1. **CI ainda não executado.** Cada comando roda localmente e está verde; o
   GitHub Actions só roda após o primeiro `push`, que continua não autorizado.
2. **A fidelidade do contexto histórico depende de um teste, e não do
   compilador.** O recorte em memória repete a lógica do provedor de produção;
   se um dos dois mudar sozinho, quem acusa é
   `Candidato_igual_ao_vigente_reproduz_as_decisoes_ja_gravadas`.
3. **O backtest carrega o período inteiro na memória.** Acima de 50.000
   transações de contexto ele falha em vez de degradar — correto, mas é um teto
   e não uma paginação.
4. **A lista de backtests não pagina na tela.** A API pagina e devolve o total;
   a interface mostra a primeira página. Mesma dívida das telas de casos.
5. **Execuções não expiram e não são apagadas.** A tabela cresce sem teto.
6. **Verificação visual no navegador segue pendente.** O último passo exige
   digitar a senha de sessão no formulário, o que o agente não faz. Débito
   herdado da Fase 2.

---

# FASE 10 — OPERAÇÃO, BUSCA, PAINEL E AUDITORIA

## 10.1 Objetivo

Completar a experiência diária do operador e do auditor.

Ao final:

> a Central Antifraude funciona como console operacional completo, não apenas coleção de endpoints.

---

## 10.2 Transações

Aprimorar lista com:

- busca;
- filtros;
- faixa de score;
- decisão;
- data;
- cliente externo;
- regra/sinal quando útil;
- paginação;
- ordenação.

Filtros devem ser construídos de forma tipada/segura.

---

## 10.3 Detalhe da transação

Consolidar:

- entrada sanitizada;
- horários;
- avaliação;
- sinais;
- regra/version;
- profile version;
- alertas;
- casos relacionados;
- resultado humano quando existir;
- correlation ID quando apropriado para suporte.

---

## 10.4 Painel Operacional

Responder:

- quantas transações chegaram;
- proporção Permitir/Revisar/Bloquear;
- alertas abertos;
- casos em aberto;
- casos antigos;
- sinais mais frequentes;
- tendência temporal;
- decisões recentes.

Evitar métricas decorativas.

---

## 10.5 Métricas de regra

Mostrar, com definições claras:

- acionamentos;
- resultados conhecidos;
- fraude confirmada;
- legítima;
- inconclusiva.

Não vender correlação como causalidade.

---

## 10.6 Auditoria

Criar tela/API de consulta append-only com:

- ator;
- operação;
- entidade;
- timestamp;
- dados seguros de antes/depois quando aplicável;
- filtros.

Não incluir secrets ou payloads sensíveis completos.

---

## 10.7 Exportação

Só implementar CSV/export se houver utilidade concreta e escopo seguro.

Se entrar:

- streaming/paginação;
- limite;
- autorização;
- tenant;
- fórmula CSV injection mitigada.

Não é obrigatória.

---

## 10.8 Security Gate 10

Testar:

- filtros com SQLi;
- paginação abusiva;
- busca cross-tenant;
- auditoria vazando secrets;
- CSV injection se export existir;
- dashboard misturando tenants;
- enum injection;
- ordenação por campo arbitrário.

---

## 10.9 Critérios de conclusão

- [x] Console de transações completo.
- [x] Detalhe consolidado.
- [x] Painel responde perguntas operacionais reais.
- [x] Auditoria consultável.
- [x] Métricas definidas corretamente.
- [x] Consultas eficientes o suficiente para baseline.
- [x] Security Gate 10 verde.
- [ ] CI verde — *pendente do primeiro `push`, que não foi autorizado. Cada passo do workflow foi executado localmente e está verde.*

---

## 10.10 Resultado da Fase 10

**Concluída em 2026-09-07.**

### Evidências

| Critério | Como foi verificado |
|---|---|
| Build backend | `dotnet build -c Release`, **0 erros e 0 avisos** |
| Build frontend | `npm run build`, `tsc`, `oxlint` e `prettier --check` limpos |
| Console completo | busca, decisão, tipo de sinal, faixa de score, período, ordenação por 4 campos e paginação — tudo no servidor, numa consulta só |
| **Total acompanha o filtro** | as três decisões filtradas somam o total sem filtro; sem isso a paginação mostraria páginas vazias no fim |
| Detalhe consolidado | avaliação, alertas com o caso que os recolheu, veredito humano e o identificador de correlação |
| Painel | recebidas × avaliadas, três decisões sempre visíveis, tendência sem buracos, fila humana, casos parados e eventos pendentes na Outbox |
| Métricas de regra | acionamentos cruzados com veredito; **as quatro colunas somam exatamente os acionamentos**, e nenhuma taxa é calculada |
| Auditoria consultável | trilha cronológica, filtro por operação com o vocabulário que existe no tenant, e **nenhuma rota que a altere** |
| Correlação ponta a ponta | o identificador da transação é o mesmo que a Outbox gravou — conferido no banco |
| Testes | 519 unitários + 25 arquitetura + 410 integração + 134 frontend = **1.088, todos verdes** |
| Security Gate 10 | [`docs/security-gate-10.md`](docs/security-gate-10.md) |
| Migration | `ConsoleOperacional` aplicada em PostgreSQL real; `has-pending-model-changes` sem alteração pendente |
| Dependências | `dotnet list package --vulnerable`: nenhuma; `npm audit`: 0 |
| Segredos | `gitleaks detect` e `detect --no-git`: nenhum |

### Decisões congeladas

| Decisão | Registro |
|---|---|
| Filtrar, ordenar e paginar são a mesma consulta | `docs/adr/0015-console-operacional.md` |
| A busca livre é literal: curingas de `LIKE` escapados | `docs/adr/0015` |
| Console filtra por ocorrência; painel conta por avaliação | `docs/adr/0015` |
| O painel conta ao vivo; a projeção diária mantém o papel da Fase 5 | `docs/adr/0015` |
| Métricas de regra são contagens com denominador, nunca taxa | `docs/adr/0015` |
| A trilha é lida por Administrador e Auditor, e por mais ninguém | `docs/adr/0015` |
| A transação guarda o identificador de correlação | `docs/adr/0015` |
| O detalhe reúne avaliação, alerta, caso e veredito numa consulta | `docs/adr/0015` |
| Recusar, nunca corrigir em silêncio | `docs/adr/0015` |
| Sem exportação — decisão, e não esquecimento | `docs/adr/0015` |

### Uma decisão da Fase 5 foi revertida, com motivo

`ResumoDiarioDeDecisoes` foi criada dizendo "a Fase 10 usa esta tabela no painel
operacional". Não usa.

A projeção é alimentada pelo caminho assíncrono e fica para trás quando a fila
atrasa. Um painel lendo dela discordaria da lista de transações, e **duas
respostas para a mesma pergunta geram investigação sobre um problema que não
existe** (`CLAUDE.md` seção 44). No envelope de portfólio, contar ao vivo custa
um `GROUP BY` sobre um índice que já existe.

A projeção mantém o papel que sempre teve: ser o efeito assíncrono que prova a
idempotência do consumidor. No lugar dela, o painel expõe algo que ela não
responde — quantos eventos ainda não saíram da Outbox, que é a medida direta de
fila parada.

### Defeitos reais encontrados e corrigidos

**1. `AutoInclude` dentro de junção à esquerda derruba a consulta inteira.** A
avaliação traz os sinais por `AutoInclude`, e uma coleção dentro de um
`LEFT JOIN` faz o EF Core recusar a tradução — a listagem inteira respondia
`500`. `IgnoreAutoIncludes` não é otimização aqui: é a condição para a consulta
existir. A listagem também não mostra sinal nenhum; quem precisa deles é o
detalhe, que busca a avaliação pelo caminho próprio.

**2. O par intermediário da junção não pode ser um `record` posicional.** Com o
par projetado em `new Linha(a, b)`, o `ORDER BY` vira
`new Linha(...).Transacao.RecebidaEm` e o EF não enxerga através do construtor —
a consulta deixa de ser traduzível de novo, com outro `500`. Um tipo **anônimo**
resolve, porque o EF o trata de forma especial. A consequência é que a junção e
a projeção precisam ficar no mesmo método, e isso está documentado no código.

**3. Dois testes afirmavam números do cenário, e não a semântica do filtro.** As
primeiras versões de `O_filtro_de_decisao_...` e `O_filtro_de_score_...`
esperavam totais fixos que dependiam de quantas transações o cenário criava.
Reescritos para afirmar o que o filtro promete: as três decisões filtradas somam
o total sem filtro, e todo item devolvido respeita a borda pedida. Passaram a
provar o comportamento em vez da aritmética da montagem.

### Fora de escopo, deliberadamente

- **Sem exportação CSV.** O ROADMAP 10.7 a torna opcional, e ela adicionaria
  superfície de CSV injection e um caminho de saída em massa para resolver um
  problema que ninguém tem hoje. O item correspondente do Security Gate 10 é
  **não aplicável**, e não pendente.
- **Sem busca por texto em casos e notas.** O console cobre transações, que é
  onde a investigação começa.
- **Sem filtro por integração.** Nenhuma organização tem mais de uma hoje.
- **Sem gráfico com biblioteca.** A tendência são três números por dia e um teto
  conhecido; uma dependência a mais não resolveria problema de domínio nenhum.

### Débito técnico não bloqueante

1. **CI ainda não executado.** Cada comando roda localmente e está verde; o
   GitHub Actions só roda após o primeiro `push`, que continua não autorizado.
2. **O painel dispara oito consultas agregadas por carregamento.** São `count` e
   `GROUP BY` sobre índices existentes, no envelope de portfólio. Se o volume
   crescer, é aqui que se mede primeiro.
3. **A busca livre é `ILIKE '%termo%'` e não usa índice.** É busca de
   investigação, sobre o conjunto já restrito ao tenant. Um índice de trigrama
   resolveria, e não se justifica sem medição.
4. **A projeção diária ficou sem leitor no produto.** Continua sendo o efeito
   que prova a idempotência do consumidor — o que já justifica a existência
   dela —, mas nenhuma tela a lê.
5. **Verificação visual no navegador segue pendente.** O último passo exige
   digitar a senha de sessão no formulário, o que o agente não faz. Débito
   herdado da Fase 2.

---

# FASE 11 — HARDENING DE SEGURANÇA APLICACIONAL

## 11.1 Objetivo

Revisar sistematicamente a superfície construída.

Não é a primeira vez que segurança aparece.

É a fase de consolidação.

---

## 11.2 Threat model

Documentar fluxos e ameaças:

- browser → API;
- integração → API;
- API → PostgreSQL;
- API → Outbox;
- SQS → worker;
- worker → banco;
- admin → regras;
- backtest;
- credenciais;
- auditoria.

---

## 11.3 Autenticação

Revisar:

- access token lifetime;
- refresh token storage;
- rotation;
- reuse;
- revogação;
- logout;
- múltiplas sessões se suportadas;
- invalid claims;
- clock skew.

---

## 11.4 Browser security

Validar no deployment model planejado:

- CORS;
- Origin;
- cookies;
- SameSite;
- Secure;
- HttpOnly;
- CSRF;
- F5/refresh;
- session restoration.

Não repetir bug cross-origin do projeto anterior.

---

## 11.5 Integrações

Revisar:

- API keys;
- rotação;
- revogação;
- hash;
- rate limiting;
- replay;
- idempotency key;
- logs;
- brute force.

---

## 11.6 Authorization matrix

Criar matriz clara por perfil e endpoint/caso de uso.

Executar testes automatizados contra todas as rotas relevantes.

---

## 11.7 Input security

Revisar:

- strict JSON;
- unknown fields;
- mass assignment;
- tamanho;
- strings;
- Unicode;
- XSS;
- SQLi;
- path/query injection;
- enum binding.

---

## 11.8 Multi-tenancy review

Executar suíte cross-tenant abrangente para:

- transações;
- avaliações;
- alertas;
- casos;
- notas;
- regras;
- profiles;
- backtests;
- auditoria;
- integrações;
- usuários.

---

## 11.9 Dependências e secrets

Executar:

- dependency audit;
- secret scanning;
- configuração segura;
- revisão de logs;
- revisão de exemplos/documentos.

---

## 11.10 IA

Se IA ainda não tiver sido adicionada:

> nenhuma ação.

Não incluir IA só nesta fase para marcar checkbox.

Se houver decisão futura explícita:

- criar threat model separado de prompt injection e data leakage.

---

## 11.11 Critérios de conclusão

- [x] Threat model documentado.
- [x] Authorization matrix completa.
- [x] Rotas protegidas e testadas.
- [x] Cross-tenant suite abrangente.
- [x] CSRF/CORS/session testados de acordo com deploy.
- [x] Rate limits configurados.
- [x] Secrets auditados.
- [x] Dependency audit sem risco crítico não tratado.
- [x] Security Gate 11 verde.
- [ ] CI verde — *pendente do primeiro `push`, que não foi autorizado. Cada passo do workflow foi executado localmente e está verde.*

---

## 11.12 Resultado da Fase 11

**Concluída em 2026-09-07.**

### Evidências

| Critério | Como foi verificado |
|---|---|
| Build backend | `dotnet build -c Release`, **0 erros e 0 avisos** |
| Build frontend | `npm run build`, `tsc`, `oxlint` e `prettier --check` limpos |
| Threat model | 7 fluxos, com ameaça, mitigação e teste em cada linha — [`docs/threat-model.md`](docs/threat-model.md) |
| **Matriz de autorização** | tabela declarada **comparada com as rotas que a aplicação expõe**, lidas do roteamento; 5 invariantes |
| Rotas protegidas | 52 rotas na matriz; 5 anônimas, todas justificadas |
| **Cross-tenant** | 8 famílias, identificadores **reais**, e o estado do outro tenant conferido no banco depois das tentativas |
| CORS e sessão | origem declarada autorizada, origem estranha sem cabeçalho, curinga ausente, `Origin` recusado no refresh |
| Rate limits | login, ingestão e — novos — refresh e emissão de credencial, este por organização |
| Entrada | campo desconhecido, mass assignment, byte nulo, corpo acima de 64 KB (`413`) e corpo legítimo de 4 KB |
| Secrets | `gitleaks detect` e `detect --no-git`: nenhum |
| Dependências | `dotnet list package --vulnerable`: nenhuma; `npm audit`: 0 |
| Testes | 519 unitários + 25 arquitetura + 440 integração + 134 frontend = **1.118, todos verdes** |
| Security Gate 11 | [`docs/security-gate-11.md`](docs/security-gate-11.md) |
| Migration | nenhuma nesta fase; `has-pending-model-changes` sem alteração pendente |

### Decisões congeladas

| Decisão | Registro |
|---|---|
| CORS com lista explícita, sem curinga, compartilhando a lista da verificação de `Origin` | `src/CentralAntifraude.Api/Seguranca/PoliticaDeCors.cs` |
| Lista de origens vazia **desliga** o CORS — o estado de desenvolvimento | idem |
| Três cabeçalhos de segurança; CSP e HSTS pertencem a quem serve HTML | `CabecalhosDeSeguranca.cs` |
| Teto de corpo em três camadas, com `413` e não `400` | `MiddlewareDeLimiteDeCorpo` |
| Limitador **depois** da autenticação, para particionar por organização | `Program.cs` |
| A matriz de autorização é um teste que lê o roteamento, não um documento | `MatrizDeAutorizacaoTests.cs` |
| Nenhuma rota de escrita pode carregar `perfil:qualquer` | idem |
| Sem IA, e portanto sem ação — o ROADMAP 11.10 proíbe adicionar para marcar item | — |

### Quatro lacunas concretas, e não revisão genérica

1. **Não havia CORS.** Até aqui o proxy do Vite fazia tudo parecer mesma
   origem; a Fase 14 quebra isso. Entrou agora, com a **mesma lista** que já
   defendia contra CSRF — duas listas sairiam de sincronia no primeiro ajuste,
   e o sintoma seria o navegador aceitando a resposta enquanto o servidor
   recusa a operação.
2. **`refresh` e emissão de credencial não tinham limite**, apesar de nomeados
   no `CLAUDE.md` seção 55.
3. **Nenhuma rota humana tinha teto de corpo.** Só a ingestão declarava 8 KB;
   o resto herdava os 30 MB do Kestrel.
4. **A matriz de autorização não existia como verificação** — era conhecimento
   espalhado por dez gates.

### Defeito real encontrado e corrigido

**O limitador rodava antes da autenticação, e a partição por organização não
funcionava.** A emissão de credencial é particionada pela organização da
identidade; antes de `UseAuthentication`, `HttpContext.User` está vazio e a
partição caía no IP — a cota virava **global**, e a conta comprometida de um
cliente travaria a operação de todos os outros.

O teste que pegou isso é o que afirma o oposto do óbvio: depois de A esgotar a
própria cota, B continua sendo atendido. Um teste que só verificasse "o limite
funciona" teria passado com o defeito no lugar.

### Fora de escopo, deliberadamente

- **Sem IA.** O ROADMAP 11.10 é explícito: não adicionar IA nesta fase para
  marcar item. Não há IA no produto, e a seção 61 do `CLAUDE.md` já define as
  regras para o dia em que houver.
- **Sem CSP nem HSTS.** Pertencem a quem serve HTML, que a partir da Fase 14 é
  a Vercel. Uma política de conteúdo para respostas JSON que nunca são
  renderizadas seria configuração sem problema para resolver.
- **Sem pentest.** É a Fase 15.
- **Sem HMAC de request.** Para cliente-servidor sobre TLS com credencial
  própria e idempotência, assinar o payload não resolve problema que já não
  esteja resolvido (`CLAUDE.md` seção 51).

### Débito técnico não bloqueante

1. **CI ainda não executado.** Cada comando roda localmente e está verde; o
   GitHub Actions só roda após o primeiro `push`, que continua não autorizado.
2. **Rate limiting é por instância do processo.** Com várias instâncias em
   Lambda o limite efetivo se multiplica. Tratamento distribuído exige estado
   compartilhado, que a arquitetura não tem — fica como limitação conhecida, e
   não como pendência.
3. **CORS e cookie cross-site testados contra a configuração, e não contra o
   deploy.** O deploy é a Fase 14, e a ordem foi deliberada: chegar lá com o
   comportamento já verificado.
4. **A equalização de tempo no login continua sendo por construção**, e não
   medida. Débito herdado da Fase 1.
5. **Verificação visual no navegador segue pendente.** Débito herdado da
   Fase 2.

---

# FASE 12 — OBSERVABILIDADE, RESILIÊNCIA E PERFORMANCE

## 12.1 Objetivo

Provar que o sistema pode ser operado e diagnosticado.

---

## 12.2 Logs estruturados

Padronizar:

- CorrelationId;
- EventId;
- EvaluationId;
- operação;
- decisão;
- rule type;
- duração;
- status.

Não registrar:

- API key;
- refresh token;
- PAN/CVV;
- payload completo;
- nota privada desnecessária.

---

## 12.3 Correlation

Demonstrar fluxo ponta a ponta:

```text
request
→ evaluation
→ outbox
→ publish
→ worker
→ alert
```

Uma investigação técnica deve conseguir seguir o mesmo CorrelationId.

---

## 12.4 Métricas

Implementar métricas úteis e baixa cardinalidade:

- evaluations total;
- evaluation duration;
- decisions total;
- outbox pending;
- outbox age;
- worker failures;
- DLQ;
- alerts created;
- backtest duration;
- serializable retries.

---

## 12.5 Health/readiness

Separar quando fizer sentido:

- processo vivo;
- dependência pronta.

Não expor internamente detalhes sensíveis.

---

## 12.6 Resiliência

Testar:

- banco temporariamente indisponível;
- SQS indisponível no dispatcher;
- worker falhando;
- poison message;
- DLQ;
- outbox acumulado;
- retry de serialization;
- cancelamento HTTP.

Não adicionar retry genérico.

---

## 12.7 Testes de carga

Criar cenários reproduzíveis.

### Cenário A — throughput normal

Múltiplos clientes independentes.

### Cenário B — hot customer

Muitas transações concorrentes do mesmo customer.

### Cenário C — idempotency storm

Mesmo request repetido simultaneamente.

### Cenário D — backlog async

Eventos acumulados e recuperação.

Medir, não inventar SLA.

---

## 12.8 Query performance

Inspecionar queries críticas.

Adicionar/ajustar índices somente com evidência.

Avaliar especialmente:

- janela temporal por cliente;
- dispositivo;
- instrumento;
- alertas abertos;
- casos;
- dashboard.

---

## 12.9 Decisão Redis

Nesta fase existe um checkpoint explícito:

> Os benchmarks demonstraram necessidade concreta de cache/distributed state?

Se **não**:

> documentar que PostgreSQL continua suficiente.

Se **sim**:

> parar antes de introduzir Redis, pois isso reabre decisão arquitetural do `CLAUDE.md`.

---

## 12.10 Critérios de conclusão

- [ ] Correlation ponta a ponta.
- [ ] Structured logs seguros.
- [ ] Métricas úteis.
- [ ] DLQ observável.
- [ ] Cenários de falha testados.
- [ ] Cenários de carga executados.
- [ ] Queries críticas analisadas.
- [ ] Índices justificados.
- [ ] Retry não genérico.
- [ ] Decisão sobre Redis baseada em evidência.
- [ ] CI verde.

---

# FASE 13 — DADOS DE DEMONSTRAÇÃO E UX FINAL

## 13.1 Objetivo

Transformar o sistema tecnicamente completo em um produto demonstrável.

---

## 13.2 Seed narrativo

Criar tenant demo fictício com histórias intencionais.

Personagens/dados podem ser ajustados, mas devem cobrir:

### História A — comportamento normal

Cliente antigo, dispositivo conhecido, valor comum.

Resultado:

> Permitir.

### História B — valor anormal + novo dispositivo

Resultado esperado configurado:

> Revisar ou Bloquear conforme profile demo.

### História C — velocidade

Múltiplas tentativas em curto período.

### História D — divergência geográfica

Sinal claro e explicável.

### História E — falso positivo

Decisão automática:

> Revisar.

Investigação:

> Legítima.

### História F — caso com múltiplos alertas

Demonstra agrupamento e timeline.

---

## 13.3 Seed determinístico

Seed deve ser:

- reproduzível;
- idempotente;
- sem PII real;
- sem valores aleatórios não determinísticos que quebrem screenshots/testes.

---

## 13.4 Conta demo

Planejar conta pública de demonstração com privilégios controlados.

Preferência:

> leitura ou operações seguras/resetáveis.

Não expor ações administrativas destrutivas.

Se analista demo puder alterar estado, criar estratégia de reset.

---

## 13.5 UX polish

Revisar:

- densidade;
- responsive mínimo;
- loading;
- empty state;
- error state;
- foco;
- keyboard basics;
- contraste;
- tabelas;
- filtros;
- timeline;
- hierarquia visual.

Sem transformar em landing page.

---

## 13.6 Acessibilidade

Validar itens principais:

- labels;
- keyboard;
- focus;
- headings;
- table semantics;
- contrast;
- modals/dialogs;
- status não dependente só de cor.

---

## 13.7 Storytelling

Sequência de demonstração deve permitir:

```text
Painel
→ transação suspeita
→ score/sinais
→ alerta
→ caso
→ timeline
→ resolução
→ regra
→ backtest
→ auditoria
```

---

## 13.8 Critérios de conclusão

- [ ] Seed conta histórias reais do produto.
- [ ] Existe falso positivo explícito.
- [ ] Dados 100% fictícios.
- [ ] Demo reproduzível.
- [ ] Fluxos críticos possuem loading/error/empty.
- [ ] UX parece console operacional.
- [ ] Acessibilidade básica revisada.
- [ ] Storytelling pronto para recrutador.
- [ ] CI verde.

---

# FASE 14 — INFRAESTRUTURA, CUSTO E DEPLOY

## 14.1 Objetivo

Preparar e, somente após autorização explícita, publicar a aplicação.

---

## 14.2 Regra de autorização

A fase pode preparar tudo localmente.

Antes de:

- criar recurso AWS;
- deployar Vercel;
- aplicar migration de produção;
- criar Neon production;
- publicar Function URL;

confirmar autorização explícita conforme `CLAUDE.md`.

---

## 14.3 Pricing review

Antes do deploy, verificar pricing atual de:

- Lambda;
- SQS;
- EventBridge Scheduler;
- CloudWatch;
- SSM;
- Neon;
- Vercel.

Não confiar em valores antigos do roadmap.

---

## 14.4 Estimativa de custo

Usar envelope realista:

- ~10.000 avaliações/mês;
- ~10.000 eventos/mês;
- até ~100 backtests/mês;
- logs controlados;
- banco pequeno.

Documentar:

- requests;
- duração;
- armazenamento;
- logs;
- scheduler;
- tráfego;
- custo estimado.

Objetivo:

> **AWS esperado = US$ 0,00 para uso de portfólio.**

---

## 14.5 IaC

Infra AWS deve ser reproduzível.

Preferência já definida:

- AWS SAM / CloudFormation.

Criar:

- API Lambda/Function URL;
- worker de eventos;
- worker de backtest;
- dispatcher;
- filas;
- DLQs;
- scheduler;
- parâmetros;
- logs;
- alarmes mínimos quando úteis.

---

## 14.6 Banco

Preparar Neon:

- conexão segura;
- pooling apropriado;
- migrations;
- least privilege quando viável;
- backups/branch strategy conforme plano atual.

Não depender de RDS.

---

## 14.7 Frontend

Vercel:

- build;
- env;
- domínio/subdomínio se escolhido;
- headers/security quando aplicáveis.

---

## 14.8 Browser real

Testar em produção:

- login;
- refresh;
- F5;
- CORS;
- CSRF;
- cookies;
- sessão;
- ingestão de integração;
- fluxo de alerta/caso;
- backtest;
- auditoria.

---

## 14.9 Cloud production-like validation

Validar características reais:

- runtime Lambda;
- globalization;
- timezone;
- cold start;
- connection behavior;
- SQS;
- retries;
- DLQ;
- logs.

Todo bug de ambiente relevante deve ganhar teste automatizado quando razoável.

---

## 14.10 Cost guardrails

Configurar:

- AWS Budget/alerta;
- retenção de logs adequada;
- limites de concorrência se necessários;
- backtest limits;
- scheduler em frequência mínima necessária.

---

## 14.11 Critérios de conclusão

- [ ] Pricing atual verificado.
- [ ] Estimativa documentada.
- [ ] IaC reproduzível.
- [ ] Secrets fora do código.
- [ ] Budget/alerta configurado quando possível.
- [ ] Produção deployada somente com autorização.
- [ ] Browser flows validados.
- [ ] Async flows validados em AWS.
- [ ] Runtime real reproduzido em testes quando necessário.
- [ ] Custo esperado dentro da meta.
- [ ] CI verde.

---

# FASE 15 — VALIDAÇÃO FINAL, PENTEST, DOCUMENTAÇÃO E RELEASE

## 15.1 Objetivo

Congelar a Central Antifraude como projeto final de portfólio.

Não adicionar novo módulo nesta fase.

---

## 15.2 Revisão funcional

Executar jornada completa:

```text
criar/usar integração
→ enviar transação
→ receber decisão
→ visualizar sinais
→ gerar alerta
→ criar caso
→ investigar
→ resolver
→ consultar feedback
→ editar draft de regra
→ backtest
→ publicar nova versão
→ confirmar histórico
→ auditoria
```

---

## 15.3 Regressão

Executar toda a suíte:

- backend;
- frontend;
- integration;
- architecture;
- security;
- concurrency;
- workers;
- end-to-end quando adotado.

---

## 15.4 Pentest gray-box autorizado

Cobrir, no mínimo:

- JWT;
- refresh replay;
- API key;
- integration replay;
- IDOR;
- cross-tenant;
- tenant injection;
- privilege escalation;
- SQL injection;
- XSS;
- CSRF;
- rate limiting;
- mass assignment;
- strict JSON;
- rule manipulation;
- outbox/event manipulation em superfícies acessíveis;
- backtest abuse;
- secrets;
- logs.

Se IA não existir:

> não inventar teste de prompt injection.

---

## 15.5 Limitações do pentest

Documentar explicitamente:

- escopo;
- data;
- ambiente;
- credenciais usadas;
- o que não foi testado;
- ferramentas;
- limitações;
- resultados.

Nunca afirmar:

> “100% seguro”.

---

## 15.6 Dependabot/dependency review

Triar atualizações.

Não fazer major upgrade cego na fase final.

Merge somente quando:

- risco compreendido;
- testes verdes;
- compatibilidade confirmada.

---

## 15.7 README final

Estrutura sugerida:

1. Central Antifraude;
2. demonstração;
3. o problema;
4. como funciona;
5. decisões de risco;
6. investigação;
7. regras explicáveis;
8. backtest;
9. arquitetura;
10. idempotência;
11. concorrência;
12. Outbox/SQS;
13. segurança;
14. observabilidade;
15. testes;
16. stack;
17. execução local;
18. deploy;
19. custo;
20. limitações;
21. screenshots;
22. documentação técnica.

---

## 15.8 Screenshots

Selecionar telas que vendem produto:

- Painel;
- Transação + sinais;
- Alertas;
- Caso;
- Regra;
- Backtest;
- Auditoria.

Não encher README com CRUD administrativo.

---

## 15.9 Vídeo

Criar demonstração curta.

Meta aproximada:

> 25–40 segundos.

Sequência sugerida:

```text
Login/demo
→ Painel
→ Transação suspeita
→ sinais/score
→ alerta
→ caso/timeline
→ backtest
→ auditoria
```

Sem terminal ou AWS console.

Produto no vídeo.

Engenharia no GitHub.

---

## 15.10 Metadata GitHub

Preparar:

- description;
- website;
- topics;
- social preview se aplicável.

Não declarar feature inexistente.

---

## 15.11 Release

Criar, após autorização:

> `Central Antifraude v1.0.0 — Portfolio Release`

Tag:

```text
v1.0.0
```

Depois da release:

> congelar escopo.

Reabrir apenas para:

- bug real;
- segurança;
- dependência relevante;
- manutenção necessária.

---

## 15.12 Critérios de conclusão

- [ ] Jornada completa validada.
- [ ] Todas as suítes verdes.
- [ ] Pentest executado e documentado.
- [ ] Nenhuma vulnerabilidade crítica conhecida aberta.
- [ ] README final.
- [ ] Screenshots.
- [ ] Vídeo.
- [ ] GitHub metadata.
- [ ] CI final verde.
- [ ] Release `v1.0.0`.
- [ ] Projeto congelado.

---

# 5. SECURITY GATES — RESUMO

| Gate | Principal risco coberto |
|---|---|
| Gate 0 | secrets, configuração e baseline |
| Gate 1 | auth humana, RBAC e tenancy |
| Gate 2 | integração, replay e mass assignment |
| Gate 3 | motor e campos internos |
| Gate 4 | races, serialização e replay |
| Gate 5 | eventos duplicados, poison messages, DLQ |
| Gate 6 | alertas e isolamento |
| Gate 7 | casos, XSS, ownership e workflow |
| Gate 8 | publicação/versionamento de regras |
| Gate 9 | backtest abuse e isolamento |
| Gate 10 | filtros, auditoria e consultas |
| Gate 11 | hardening completo |
| Gate 12+ | validação operacional e produção |
| Final | pentest gray-box |

---

# 6. INVARIANTES QUE DEVEM GANHAR TESTE AUTOMATIZADO

Ao final do projeto, devem existir provas automatizadas para pelo menos:

## 6.1 Idempotência

```text
mesma transaction + requests concorrentes
→ uma transação
→ uma avaliação
```

## 6.2 Replay

```text
request original
→ rule profile muda
→ replay do request
→ resultado original
```

## 6.3 External ID

```text
mesmo external ID + conteúdo conflitante
→ conflito
```

## 6.4 Concorrência

```text
transações simultâneas do mesmo cliente
→ execução equivalente a uma ordem serial válida
```

## 6.5 Versões

```text
rule v1 avalia transaction
→ v2 publicada
→ transaction histórica continua explicável por v1
```

## 6.6 Outbox

```text
commit do domínio
→ falha antes do broker
→ evento continua recuperável
```

## 6.7 Inbox

```text
mesmo event N vezes
→ efeito único
```

## 6.8 Alert

```text
TransactionEvaluated duplicado
→ um alerta
```

## 6.9 Cross-tenant

```text
tenant A conhece ID de B
→ não acessa
```

## 6.10 Backtest

```text
backtest
→ nenhuma RiskEvaluation real alterada
→ nenhum Alert real criado
```

## 6.11 Investigação

```text
Revisar
→ caso
→ resultado Legítima
```

deve ser um fluxo válido.

## 6.12 Auditoria

Operação crítica:

> gera registro append-only e não editável por endpoint comum.

---

# 7. O QUE NÃO ENTRA EM NENHUMA FASE DA V1

Não adicionar por iniciativa própria:

- machine learning antifraude;
- modelo de IA classificando fraude;
- graph database;
- fraud ring detection avançada;
- behavioral biometrics;
- KYC;
- AML;
- chargeback;
- payment processing;
- PIX;
- 3DS;
- cartão real;
- device fingerprint comercial;
- Kafka;
- microserviços;
- Kubernetes;
- RDS;
- Redis sem benchmark justificar;
- Event Sourcing;
- CQRS completo;
- OpenSearch;
- data lake;
- data warehouse.

Se uma necessidade concreta surgir:

> seguir regra de parada do `CLAUDE.md` para mudança arquitetural/escopo.

---

# 8. IA — STATUS DA V1

IA **não é requisito da v1**.

A Central Antifraude deve chegar a `v1.0.0` sem depender de IA.

Somente reconsiderar se, depois de o workspace de investigação estar funcional, houver um problema real como:

> casos longos demais para leitura rápida.

Possível extensão futura:

- resumo de caso;
- resumo de timeline;
- explicação textual dos sinais já calculados.

Mesmo assim:

> IA nunca entra no score nem decide fraude.

---

# 9. DEFINITION OF DONE GLOBAL

Além dos critérios específicos, toda fase autorizada deve terminar com:

- build verde;
- testes relevantes verdes;
- nenhuma regressão conhecida introduzida;
- migration válida quando aplicável;
- Security Gate da fase verde;
- documentação factual atualizada;
- código compreensível;
- sem segredo no repositório;
- sem mudança arquitetural silenciosa;
- commits locais coerentes;
- sem push;
- sem deploy, salvo autorização explícita.

---

# 10. POLÍTICA DE PROGRESSO

Ao concluir uma fase:

1. atualizar o status neste arquivo;
2. registrar commits;
3. resumir:
   - o que foi entregue;
   - testes;
   - Security Gate;
   - decisões relevantes;
   - débitos não bloqueantes;
4. parar antes da próxima fase.

A autorização de uma fase não autoriza automaticamente a fase seguinte.

---

# 11. STATUS

| Fase | Status |
|---|---|
| 0 — Fundação Técnica | ✅ Concluída (2026-09-03) |
| 1 — Identidade e Multi-tenancy | ✅ Concluída (2026-09-03) |
| 2 — Integrações e Ingestão | ✅ Concluída (2026-09-04) |
| 3 — Motor de Risco v1 | ✅ Concluída (2026-09-04) |
| 4 — Avaliação Síncrona e Concorrência | ✅ Concluída (2026-09-04) |
| 5 — Backbone Assíncrono | ✅ Concluída (2026-09-04) |
| 6 — Alertas | ✅ Concluída (2026-09-05) |
| 7 — Casos e Investigação | ✅ Concluída (2026-09-05) |
| 8 — Gestão e Versionamento de Regras | ✅ Concluída (2026-09-06) |
| 9 — Backtests | ✅ Concluída (2026-09-07) |
| 10 — Operação, Busca e Auditoria | ✅ Concluída (2026-09-07) |
| 11 — Segurança Aplicacional | ✅ Concluída (2026-09-07) |
| 12 — Observabilidade, Resiliência e Performance | ⬜ Não iniciada |
| 13 — Demo e UX Final | ⬜ Não iniciada |
| 14 — Infraestrutura e Deploy | ⬜ Não iniciada |
| 15 — Validação Final e Release | ⬜ Não iniciada |

Legenda:

- ⬜ Não iniciada
- 🟨 Em andamento
- ✅ Concluída
- ⛔ Bloqueada

---

# 12. PRIMEIRA PRÓXIMA AÇÃO

Com `CLAUDE.md` e `ROADMAP.md` aprovados, a implementação começa por:

> **FASE 0 — FUNDAÇÃO TÉCNICA**

O Claude Code deve receber autorização para concluir **a Fase 0 inteira**, respeitando todas as regras do `CLAUDE.md`.

Não começar pela criação de controllers de transação ou motor de risco antes da fundação estar verde.
