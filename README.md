# Central Antifraude

Plataforma B2B para avaliação de risco, monitoramento e investigação de
transações suspeitas em pagamentos digitais.

> A Central Antifraude recebe transações de pagamentos digitais, avalia risco
> de forma determinística e explicável, retorna uma decisão imediata e processa
> os efeitos operacionais de forma assíncrona, idempotente e auditável.

**A plataforma não processa dinheiro.** Ela recomenda uma ação de risco —
`Permitir`, `Revisar` ou `Bloquear` — e oferece as ferramentas para um analista
humano investigar o que ficou em dúvida. Não é banco, gateway, adquirente nem
solução certificada de compliance.

---

## Estado atual

| Fase | Nome | Status |
|---|---|---|
| 0 | Fundação Técnica | ✅ concluída |
| 1 | Identidade e Multi-tenancy | ✅ concluída |
| **2** | **Integrações e Ingestão** | ✅ **concluída** |
| 3 | Motor de Risco v1 | não iniciada |
| 4–15 | — | ver [`ROADMAP.md`](ROADMAP.md) |

Hoje a plataforma recebe transações de sistemas externos autenticados por
credencial própria, registra cada tentativa **exatamente uma vez** mesmo sob
requisições simultâneas, e mantém o isolamento entre organizações.

**Ainda não existe** motor de risco, alertas nem investigação. Isso é
deliberado — cada capacidade chega na fase que o `ROADMAP.md` define.

---

## Stack

**Backend** — C#, .NET 10, ASP.NET Core (Minimal APIs), Entity Framework Core
**Frontend** — React 19, TypeScript, Vite, TanStack Query, React Router
**Banco** — PostgreSQL 17
**Testes** — xUnit v3, Testcontainers, Vitest, Testing Library

---

## Estrutura

```text
src/
  CentralAntifraude.Domain/          invariantes e primitivos — zero dependência
  CentralAntifraude.Application/     contratos e casos de uso
  CentralAntifraude.Infrastructure/  PostgreSQL, EF Core, relógio
  CentralAntifraude.Api/             composição HTTP

tests/
  CentralAntifraude.UnitTests/         domínio, aplicação, contrato de erro
  CentralAntifraude.IntegrationTests/  PostgreSQL real, isolamento e Security Gates
  CentralAntifraude.ArchitectureTests/ regras de dependência entre camadas

frontend/                            shell React + Vite

docs/
  adr/                               decisões arquiteturais
  setup-local.md                     como rodar
  security-gate-0.md                 resultado do gate da Fase 0
  security-gate-1.md                 resultado do gate da Fase 1
  security-gate-2.md                 resultado do gate da Fase 2
```

A dependência corre em uma direção só, e testes de arquitetura seguram isso:

```text
Api  ──────►  Application  ──────►  Domain
 │                                    ▲
 └──────►  Infrastructure  ───────────┘
```

---

## Executar

Guia completo em [`docs/setup-local.md`](docs/setup-local.md), incluindo as
armadilhas conhecidas de ambiente Windows.

```bash
cp .env.example .env          # ajuste POSTGRES_PASSWORD
docker compose up -d

export ConnectionStrings__Postgres="Host=localhost;Port=5434;Database=central_antifraude;Username=central_antifraude;Password=SUA_SENHA"
dotnet tool restore
dotnet dotnet-ef database update --project src/CentralAntifraude.Infrastructure

dotnet run --project src/CentralAntifraude.Api    # http://localhost:5175
cd frontend && npm install && npm run dev         # http://localhost:5174
```

A string de conexão **não** está em nenhum arquivo versionado. Sem ela, a API
não sobe — e diz o motivo.

### Endpoints

Rotas de aplicação ficam sob `/api`; `/health/*` fica na raiz, porque é
consumido por orquestrador e não pela interface.

| Rota | Perfil | O que faz |
|---|---|---|
| `GET /health/live` | anônimo | o processo está vivo. Não toca em dependência externa. |
| `GET /health/ready` | anônimo | as dependências respondem. Pode falhar sozinho. |
| `POST /api/auth/login` | anônimo | autentica e emite a sessão |
| `POST /api/auth/refresh` | cookie | rotaciona a sessão |
| `POST /api/auth/logout` | cookie | encerra a sessão |
| `GET /api/auth/eu` | qualquer | dados da própria sessão |
| `GET /api/usuarios` | Administrador | lista os usuários da organização |
| `POST /api/usuarios` | Administrador | cria usuário |
| `PUT /api/usuarios/{id}/perfil` | Administrador | altera perfil e corta sessões |
| `PUT /api/usuarios/{id}/ativacao` | Administrador | ativa/desativa e corta sessões |
| `PUT /api/usuarios/{id}/senha` | Administrador | redefine senha |
| `PUT /api/usuarios/{id}/nome` | Administrador | altera nome |
| `GET /api/integracoes` | Administrador | lista integrações |
| `POST /api/integracoes` | Administrador | cria integração e emite a primeira chave |
| `GET /api/integracoes/{id}` | Administrador | integração e suas credenciais |
| `POST /api/integracoes/{id}/credenciais` | Administrador | emite chave para rotação |
| `DELETE /api/integracoes/{id}/credenciais/{cid}` | Administrador | revoga uma chave |
| `PUT /api/integracoes/{id}/ativacao` | Administrador | ativa/desativa e revoga chaves |
| `GET /api/transacoes` | qualquer | transações da organização |
| `POST /api/ingestao/transacoes` | **`ApiKey`** | recebe uma tentativa de pagamento |

A ingestão usa esquema de autenticação **próprio** (`Authorization: ApiKey ...`):
integração não é usuário, e um token humano não serve ali — nem o contrário.

---

## Testes

```bash
dotnet test tests/CentralAntifraude.UnitTests/CentralAntifraude.UnitTests.csproj
dotnet test tests/CentralAntifraude.ArchitectureTests/CentralAntifraude.ArchitectureTests.csproj
dotnet test tests/CentralAntifraude.IntegrationTests/CentralAntifraude.IntegrationTests.csproj
cd frontend && npm run test
```

Os testes de integração sobem o próprio PostgreSQL via Testcontainers e exigem
Docker no ar. SQLite não é aceito como substituto: as fases seguintes dependem
de `SERIALIZABLE`, `FOR UPDATE SKIP LOCKED` e constraints que só o PostgreSQL
tem — um teste de concorrência verde no SQLite não provaria nada.

---

## Decisões já congeladas

| Decisão | Onde |
|---|---|
| Monólito modular em quatro projetos | [ADR 0001](docs/adr/0001-monolito-modular.md) |
| Identificadores internos são UUIDv7 | [ADR 0002](docs/adr/0002-identificadores-internos.md) |
| PostgreSQL real nos testes, via Testcontainers | [ADR 0003](docs/adr/0003-postgresql-e-testes-de-integracao.md) |
| UTC, dinheiro, erros, JSON, correlação, paginação | [ADR 0004](docs/adr/0004-contratos-transversais.md) |
| E-mail único global, filtro de tenant em duas camadas, prefixo `/api` | [ADR 0005](docs/adr/0005-identidade-e-multi-tenancy.md) |
| Tokens, hash de senha, CSRF e detecção de reuso | [ADR 0006](docs/adr/0006-sessao-humana.md) |
| Idempotência em três camadas, fingerprint canônico, credencial de integração | [ADR 0007](docs/adr/0007-ingestao-e-idempotencia.md) |

Três decisões são reforçadas em tempo de compilação por
`src/BannedSymbols.txt`: `DateTime.UtcNow`, `DateTime.Now` e `Guid.NewGuid()`
**quebram o build** em `src/`. O tempo vem de `IRelogio`; o identificador vem
de `Identificador.Novo()`.

---

## Governança

- [`CLAUDE.md`](CLAUDE.md) — produto, arquitetura, segurança e invariantes.
  Autoridade permanente.
- [`ROADMAP.md`](ROADMAP.md) — quando cada capacidade é construída e o que
  torna cada fase concluída.

---

## Licença e escopo

Projeto de portfólio. Dados de demonstração são fictícios por construção: a
plataforma nunca armazena PAN, CVV ou credencial financeira — apenas
referências tokenizadas fornecidas por um sistema externo.
