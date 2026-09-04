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
| 2 | Integrações e Ingestão | ✅ concluída |
| 3 | Motor de Risco v1 | ✅ concluída |
| 4 | Avaliação Síncrona e Concorrência | ✅ concluída |
| **5** | **Backbone Assíncrono** | ✅ **concluída** |
| 6–15 | — | ver [`ROADMAP.md`](ROADMAP.md) |

Hoje a plataforma recebe transações de sistemas externos autenticados por
credencial própria, registra cada tentativa **exatamente uma vez** mesmo sob
requisições simultâneas, **avalia o risco na mesma operação** e devolve
`Permitir`, `Revisar` ou `Bloquear` com os sinais que justificam a decisão.

O motor é determinístico e versionado: a mesma transação, o mesmo histórico e a
mesma versão de perfil produzem sempre o mesmo resultado. Cada avaliação grava a
versão do perfil e a versão de cada regra acionada, e o banco recusa apagar o
que sustenta uma decisão — uma avaliação de meses atrás continua explicável pela
configuração daquele momento.

Tudo isso acontece **em uma única transação serializável**, com retry
deliberado: transações simultâneas do mesmo cliente produzem um resultado
equivalente a alguma ordem serial válida, e um retry do integrador devolve a
avaliação original em vez de recalcular. Cada avaliação grava também um evento
na Outbox, no mesmo commit — a publicação chega na Fase 5.

Depois de responder, o evento sai pelo caminho assíncrono: a Outbox é
publicada em uma fila, um worker consome e aplica o efeito **uma vez** — mesmo
com entrega repetida, processo caindo no meio ou vários despachantes ao mesmo
tempo.

**Ainda não existe** alerta, caso, investigação nem gestão de regras. Isso é
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
  security-gate-3.md                 resultado do gate da Fase 3
  security-gate-4.md                 resultado do gate da Fase 4
  security-gate-5.md                 resultado do gate da Fase 5
  baseline-de-performance.md         números medidos do caminho crítico
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
| `GET /api/transacoes` | qualquer | transações da organização, com score e decisão |
| `GET /api/transacoes/{id}` | qualquer | detalhe com a avaliação e os sinais |
| `GET /api/regras` | qualquer | catálogo de regras em vigor |
| `GET /api/regras/perfil` | qualquer | perfil vigente, com os limiares |
| `POST /api/ingestao/transacoes` | **`ApiKey`** | recebe uma tentativa de pagamento e devolve a decisão de risco |

A ingestão pode responder **503 com `Retry-After`** quando a disputa por
concorrência passa do orçamento de retentativas. É um convite explícito a
repetir com a mesma chave de idempotência, e não um erro do servidor.

A ingestão usa esquema de autenticação **próprio** (`Authorization: ApiKey ...`):
integração não é usuário, e um token humano não serve ali — nem o contrário.

---

## O motor de risco

Quatro regras, um catálogo **fechado e tipado**. Não há DSL, SQL configurável
nem script de usuário: configurar uma regra é escolher números dentro de um
contrato conhecido, e um tipo desconhecido lido do banco **falha alto** em vez
de virar comportamento.

| Regra | O que reconhece | Peso (demo) | Configuração (demo) |
|---|---|---|---|
| Velocidade por cliente | várias tentativas em sequência — o padrão de *card testing* | 35 | mais de 3 em 10 min |
| Dispositivo novo | aparelho nunca visto para aquele cliente | 20 | mínimo 3 no histórico |
| Valor acima do histórico | gasto muito acima do habitual daquele cliente | 30 | 5× a média, mínimo 3 |
| Divergência geográfica | país diferente dos já vistos | 25 | mínimo 3 no histórico |

| Score | Decisão |
|---|---|
| 0–39 | Permitir |
| 40–69 | Revisar |
| 70–100 | Bloquear |

As **práticas** são reais e têm fonte: U.S. Payments Forum, *"Card-Not-Present
(CNP) Fraud Mitigation Techniques"* (2020) — a citação de cada regra está no
[ADR 0008](docs/adr/0008-motor-de-risco.md).

Os **números** não são. Pesos e limiares são configuração de demonstração deste
projeto, escolhidos para que uma regra sozinha nunca bloqueie: bloquear exige ao
menos duas evidências independentes, para que a investigação humana continue
tendo propósito. Apresentá-los como padrão de mercado seria inventar.

Três propriedades que o produto garante e testa:

- **IA não participa.** Score, sinais e o texto de cada explicação são
  produzidos por código determinístico.
- **Ausência de dado não vira risco.** Sem fingerprint, sem país ou com
  histórico curto demais, a regra se cala. Tratar "não sei" como "suspeito"
  encheria a fila do analista de alertas que não dizem nada.
- **Retry não recalcula.** A mesma requisição repetida devolve a avaliação
  original, lida do banco — o mesmo pedido nunca recebe duas decisões
  diferentes.

---

## Concorrência

A avaliação lê um **conjunto** — quantas transações daquele cliente na janela — e
decide a partir dele. Duas requisições simultâneas, cada uma sem enxergar a
outra, produziriam duas avaliações que nenhuma execução sequencial produziria.

A ingestão roda numa transação **`SERIALIZABLE`**, e só ela: login, consultas e
administração não dependem de um conjunto lido para estarem corretos. Em
conflito reconhecido, a operação **inteira** é refeita — releitura do contexto e
nova avaliação, nunca a repetição do comando SQL que falhou.

O que o sistema promete, e o que não promete:

- ✅ o resultado equivale a **alguma** ordem serial válida das operações
  simultâneas;
- ❌ não se promete que toda requisição enxergue as simultâneas — isso seria
  exigir que ela enxergasse o futuro.

A prova é exata. Seis transações do mesmo cliente, com o mesmo horário de
ocorrência, enviadas ao mesmo tempo: as contagens de janela registradas têm que
ser {1..6}, sem repetido e sem buraco. Um repetido significaria duas transações
que não enxergaram uma à outra.

Quando a disputa passa do orçamento de retentativas, a resposta é **503 com
`Retry-After`** — e não 500. Repetir com a mesma chave de idempotência é seguro
por construção.

Os números medidos estão em
[`docs/baseline-de-performance.md`](docs/baseline-de-performance.md): p50 de
15 ms, p99 de 40 ms e 8 comandos SQL por ingestão.

---

## O caminho assíncrono

A decisão sai na resposta. O **efeito** sai depois — e é aí que entram as três
peças que fazem um sistema de eventos ser confiável em vez de otimista.

```text
avaliação + evento          um commit só (Outbox)
        ↓
despachante                 publica, depois marca
        ↓
fila                        entrega ao menos uma vez, sem ordem
        ↓
worker                      Inbox + efeito, um commit só
        ↓
confirma na fila            só depois do commit
```

Cada ordem acima é uma escolha, e a escolha oposta tem um custo conhecido:

| Escolha | Se fosse ao contrário |
|---|---|
| Publicar **antes** de marcar | mensagem perdida para sempre, em vez de duplicata |
| Confirmar **depois** do commit | efeito perdido, em vez de reentrega |
| Inbox no **mesmo** commit do efeito | existiria o instante em que o efeito aconteceu e a marca não |

**A promessa não é entrega única — essa ninguém cumpre. É efeito único.** O
efeito escolhido para provar isso é um contador de decisões por dia, e a
escolha é deliberada: somar duas vezes não deixa rastro nenhum. Um efeito com
chave única passaria mesmo com a Inbox desligada, porque o banco seguraria a
duplicata.

A fila é PostgreSQL até a Fase 14, reproduzindo o contrato do SQS Standard —
visibilidade, recibo por entrega, contagem de recebimentos e redrive para DLQ.
Ela é testada por um conjunto que **não cita PostgreSQL**, para que a troca pelo
SDK seja um adaptador e não uma reescrita. Detalhes no
[ADR 0010](docs/adr/0010-backbone-assincrono.md).

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
| Motor determinístico, catálogo fechado, explicabilidade gravada | [ADR 0008](docs/adr/0008-motor-de-risco.md) |
| `SERIALIZABLE` na ingestão, retry da operação inteira, Outbox | [ADR 0009](docs/adr/0009-operacao-critica-e-concorrencia.md) |
| Fila, envelope versionado, Inbox e DLQ | [ADR 0010](docs/adr/0010-backbone-assincrono.md) |

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
