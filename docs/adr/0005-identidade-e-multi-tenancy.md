# ADR 0005 — Identidade e isolamento de tenant

- **Status:** aceito
- **Data:** 2026-09-03
- **Fase:** 1 — Identidade, Organizações e Multi-tenancy

## Contexto

A Central Antifraude é um SaaS B2B multi-tenant. A invariante mais dura do
`CLAUDE.md` (seção 9) é: **dados de um tenant nunca podem ser acessados por
outro**, e o `TenantId` nunca vem do payload.

Restava decidir *como* isso é obtido na prática.

---

## Decisão 1 — E-mail é único globalmente; um usuário pertence a uma organização

O login precisa acontecer **antes** de existir tenant: descobrir a que
organização a pessoa pertence é justamente o resultado da autenticação.

Duas formas de resolver isso:

| Opção | Custo |
|---|---|
| E-mail único **global** | Uma pessoa não pode ter conta em duas organizações com o mesmo e-mail |
| E-mail + código da organização no login | Tela de login com um campo a mais, e o usuário precisa lembrar o código |

Escolhido: **e-mail único global**, com restrição única no banco.

O caso descartado — a mesma pessoa atendendo duas organizações — não existe no
produto definido: os perfis são de funcionários de uma equipe de fraude, não
de consultores externos. Se aparecer, a evolução é conhecida: uma tabela de
vínculo usuário↔organização e um seletor de organização após o login, sem
mexer no resto.

---

## Decisão 2 — Isolamento em duas camadas

### Camada 1: filtro global nomeado do EF Core

```csharp
modelBuilder.Entity<Usuario>()
    .HasQueryFilter("Tenant", u => u.OrganizacaoId == OrganizacaoAtual);
```

Isto é a rede de segurança: uma consulta futura escrita sem
`.Where(x => x.OrganizacaoId == ...)` **continua isolada**. Sem ela, bastaria
um esquecimento em uma tela para vazar dados entre clientes.

O filtro é **nomeado** (recurso do EF Core 10) para que possa ser desligado
individualmente com `IgnoreQueryFilters(["Tenant"])`. `IgnoreQueryFilters()`
sem argumento desligaria *todos* os filtros — incluindo exclusão lógica e
qualquer outro que venha depois. Seria uma forma silenciosa de abrir uma porta
que ninguém queria abrir.

**Quando o tenant é `Guid.Empty`** (requisição anônima), a comparação não casa
com nenhuma linha. O resultado é *nada*, e não *tudo*. Um bug de contexto vira
"não vejo nada" em vez de "vejo tudo" — a diferença entre um chamado de
suporte e um incidente de vazamento.

### Camada 2: testes de isolamento com dois tenants reais

Metade do que esta fase promete só pode ser provada com duas organizações
existindo ao mesmo tempo. `IsolamentoDeTenantTests` monta as duas e verifica,
com identificadores reais do outro tenant, que consulta, leitura e escrita
falham — e que o registro alvo **não mudou**.

### Os três lugares que ignoram o filtro

Todos estão em `RepositoriosDeIdentidade.cs`, nomeados e comentados:

| Método | Por quê |
|---|---|
| `BuscarPorEmailIgnorandoTenantAsync` | É o login. Descobrir a organização é o objetivo. |
| `BuscarPorIdIgnorandoTenantAsync` | É o refresh. O token chega antes de haver identidade. |
| `ExisteEmailIgnorandoTenantAsync` | A restrição única do banco é global. |

Mais os do repositório de refresh tokens, pelo mesmo motivo do segundo.

Se aparecer ali uma chamada sem justificativa, **ela é o defeito**.

---

## Decisão 3 — Recurso de outro tenant responde 404

Não 403. Um 403 significa "existe, mas você não pode" — e isso já confirma que
o identificador existe. `CLAUDE.md` seção 52.

`403` fica reservado para o caso em que o *recurso* não está em jogo: um
Auditor tentando criar usuário, por exemplo. Ali não há nada a esconder.

---

## Decisão 4 — RBAC fechado, um perfil por usuário

Quatro perfis em um enum, sem permissões configuráveis. O `ROADMAP` (seção
1.2) descarta explicitamente um sistema genérico — ele adicionaria uma
superfície grande de erro para resolver um problema que a v1 não tem.

O perfil viaja como **texto** na claim e é validado com `Enum.IsDefined`.
`Enum.TryParse` sozinho aceitaria `"7"` e devolveria `(PerfilDeUsuario)7`: um
valor que não existe, mas que passaria por qualquer comparação descuidada.

---

## Decisão 5 — Prefixo `/api` em todas as rotas de aplicação

Descoberto no navegador, não nos testes: sem prefixo, `/usuarios` seria ao
mesmo tempo uma rota do SPA e um endpoint da API. Um F5 nessa rota devolvia
**JSON no lugar da aplicação**.

Os testes de integração não pegaram porque falam com a API diretamente, sem
SPA no meio. O prefixo elimina a colisão por construção.

`/health/*` fica fora do prefixo: é consumido por orquestrador, não pela
aplicação, e não colide com rota de tela.

---

## Consequências

- Toda entidade multi-tenant nova precisa de `OrganizacaoId` e de uma linha de
  filtro no `OnModelCreating`. É repetitivo, e é o preço de a proteção não
  depender de ninguém lembrar do `WHERE`.
- Nenhuma consulta pode ignorar o filtro sem que isso apareça no nome do
  método. Foi desenhado assim para que o code review consiga perguntar por quê.
- Trocar o modelo de e-mail único global depois exigiria migração de dados;
  por isso a decisão está registrada agora, e não implícita no código.
