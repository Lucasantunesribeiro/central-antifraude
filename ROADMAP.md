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

- [ ] Integrações possuem autenticação separada de usuários.
- [ ] Tenant deriva da credencial.
- [ ] Endpoint de ingestão existe.
- [ ] Idempotency Key implementada.
- [ ] Constraint de negócio implementada.
- [ ] Replay determinístico.
- [ ] Teste concorrente verde.
- [ ] PII financeira proibida.
- [ ] Gestão administrativa mínima funcional.
- [ ] Security Gate 2 verde.
- [ ] CI verde.

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

- [ ] Motor determinístico criado.
- [ ] Catálogo inicial de regras implementado.
- [ ] Score 0..100.
- [ ] Decisão derivada de profile version.
- [ ] Sinais persistidos e explicáveis.
- [ ] Versões registradas.
- [ ] Transação antiga não depende de config atual.
- [ ] Detalhe da transação funcional no frontend.
- [ ] Fontes das práticas reais documentadas.
- [ ] Security Gate 3 verde.
- [ ] CI verde.

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

- [ ] Ingestão retorna decisão síncrona.
- [ ] Avaliação faz parte do boundary transacional correto.
- [ ] SERIALIZABLE usado somente onde justificado.
- [ ] Retry completo testado.
- [ ] Replay não recalcula avaliação.
- [ ] Teste concorrente de velocidade verde.
- [ ] Transações simultâneas não geram duplicidade.
- [ ] Evento atrasado possui semântica documentada.
- [ ] Baseline de performance registrado.
- [ ] Security Gate 4 verde.
- [ ] CI verde.

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

- [ ] Outbox transacional funcionando.
- [ ] Dispatcher idempotente/reexecutável.
- [ ] SQS Standard configurada em arquitetura.
- [ ] Inbox persistente.
- [ ] Worker idempotente.
- [ ] Correlation ID preservado.
- [ ] DLQ desenhada/testada.
- [ ] Falhas críticas simuladas.
- [ ] Security Gate 5 verde.
- [ ] CI verde.

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

- [ ] Revisar/Bloquear geram alerta segundo política.
- [ ] Evento duplicado não duplica alerta.
- [ ] Lista operacional funcional.
- [ ] Filtros server-side.
- [ ] Navegação alerta → transação.
- [ ] Isolamento de tenant testado.
- [ ] Security Gate 6 verde.
- [ ] CI verde.

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

- [ ] Caso criado a partir de alerta(s).
- [ ] Status validado.
- [ ] Ownership funcionando.
- [ ] Timeline append-only.
- [ ] Resultado obrigatório na resolução.
- [ ] Fraude/Legítima/Inconclusiva persistidos.
- [ ] Workspace de investigação utilizável.
- [ ] Falso positivo suportado corretamente.
- [ ] Security Gate 7 verde.
- [ ] CI verde.

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

- [ ] Draft editável.
- [ ] Publicação imutável.
- [ ] Rule versions navegáveis.
- [ ] Risk profile versionado.
- [ ] Concurrency control administrativo.
- [ ] Auditoria de publicação.
- [ ] Histórico antigo preservado.
- [ ] UI de regras utilizável.
- [ ] Security Gate 8 verde.
- [ ] CI verde.

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

- [ ] Backtest assíncrono.
- [ ] Fila separada.
- [ ] Mesmo RiskEngine.
- [ ] Produção não alterada.
- [ ] Resultados explicáveis.
- [ ] Limites de abuso.
- [ ] UI funcional.
- [ ] Execução duplicada tratada conforme contrato.
- [ ] Security Gate 9 verde.
- [ ] CI verde.

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

- [ ] Console de transações completo.
- [ ] Detalhe consolidado.
- [ ] Painel responde perguntas operacionais reais.
- [ ] Auditoria consultável.
- [ ] Métricas definidas corretamente.
- [ ] Consultas eficientes o suficiente para baseline.
- [ ] Security Gate 10 verde.
- [ ] CI verde.

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

- [ ] Threat model documentado.
- [ ] Authorization matrix completa.
- [ ] Rotas protegidas e testadas.
- [ ] Cross-tenant suite abrangente.
- [ ] CSRF/CORS/session testados de acordo com deploy.
- [ ] Rate limits configurados.
- [ ] Secrets auditados.
- [ ] Dependency audit sem risco crítico não tratado.
- [ ] Security Gate 11 verde.
- [ ] CI verde.

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
| 2 — Integrações e Ingestão | ⬜ Não iniciada |
| 3 — Motor de Risco v1 | ⬜ Não iniciada |
| 4 — Avaliação Síncrona e Concorrência | ⬜ Não iniciada |
| 5 — Backbone Assíncrono | ⬜ Não iniciada |
| 6 — Alertas | ⬜ Não iniciada |
| 7 — Casos e Investigação | ⬜ Não iniciada |
| 8 — Gestão e Versionamento de Regras | ⬜ Não iniciada |
| 9 — Backtests | ⬜ Não iniciada |
| 10 — Operação, Busca e Auditoria | ⬜ Não iniciada |
| 11 — Segurança Aplicacional | ⬜ Não iniciada |
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
