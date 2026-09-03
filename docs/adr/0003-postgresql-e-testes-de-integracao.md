# ADR 0003 — PostgreSQL real nos testes, via Testcontainers

- **Status:** aceito
- **Data:** 2026-09-03
- **Fase:** 0 — Fundação Técnica

## Contexto

O `CLAUDE.md` (seção 84) proíbe SQLite como substituto do PostgreSQL em teste
de integração. Este ADR registra **por quê**, para que a regra não seja
relaxada mais tarde por conveniência.

A Central Antifraude depende de comportamentos que só existem no PostgreSQL:

| Fase | Comportamento | Existe no SQLite? |
|---|---|---|
| 2 | constraint única composta e `ON CONFLICT` | parcialmente, com outra semântica |
| 4 | isolamento `SERIALIZABLE` e erro `40001` | não |
| 5 | `FOR UPDATE SKIP LOCKED` no dispatcher do outbox | não |
| 10 | planos de consulta e índices compostos | irrelevante |

Um teste de concorrência verde no SQLite não diz nada sobre o comportamento em
produção. Seria pior que não ter teste: daria confiança falsa exatamente nas
invariantes que o projeto existe para provar.

## Decisão

- **PostgreSQL 17** em todos os ambientes: container local via
  `docker-compose.yml`, container efêmero via Testcontainers nos testes, e
  Neon na Fase 14.
- Os testes de integração sobem o próprio banco com **Testcontainers**, não
  com um *service container* do GitHub Actions. O ciclo de vida pertence ao
  teste, então o mesmo comando funciona igual na máquina do desenvolvedor e no
  CI.
- Convenção de nomes **snake_case** (`EFCore.NamingConventions`). Motivo
  concreto: durante uma investigação alguém abre o `psql` e escreve consulta
  na mão. Com os nomes padrão do EF Core, cada identificador precisaria de
  aspas duplas.
- Opções do `DbContext` montadas em **um lugar só**
  (`OpcoesDoDbContext.Configurar`), usado pela API, pela fábrica de tempo de
  design e pelos testes. Se cada um montasse as suas, uma migration poderia
  ser gerada com convenção diferente da que a aplicação usa em execução.

## A migration inicial é vazia — de propósito

A Fase 0 não cria nenhuma entidade de domínio; o `ROADMAP.md` exige
explicitamente que nenhuma seja inventada só para dar o que testar.

A migration `InicialBaseline` estabelece o ponto zero do histórico. Aplicá-la
cria a tabela `__EFMigrationsHistory` e registra a linha de base — o que é
suficiente para provar que a cadeia EF Core → Npgsql → PostgreSQL →
Testcontainers funciona de ponta a ponta. As entidades começam na Fase 1.

## Consequências

- Rodar a suíte de integração **exige Docker**. Sem ele, os testes falham em
  vez de silenciosamente pularem — falhar alto é preferível a um verde que não
  executou nada.
- Cada execução leva alguns segundos a mais para subir o container. O `fixture`
  é compartilhado pela coleção de testes para pagar esse custo uma vez só.

## Verificação

`tests/CentralAntifraude.IntegrationTests/MigrationsTests.cs` cobre: aplicar do
zero, reaplicar sem duplicar, ausência de alteração de modelo pendente
(`HasPendingModelChanges`) e a convenção snake_case conferida **no banco**, via
`information_schema`, e não na configuração.
