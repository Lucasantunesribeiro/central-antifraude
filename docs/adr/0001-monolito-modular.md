# ADR 0001 — Monólito modular com quatro projetos

- **Status:** aceito
- **Data:** 2026-09-03
- **Fase:** 0 — Fundação Técnica

## Contexto

A Central Antifraude precisa provar competência em eventos, idempotência,
concorrência e explicabilidade. Nenhuma dessas coisas exige separação de
processos — todas exigem clareza de fronteira dentro do código.

O `CLAUDE.md` (seção 26) já fixa monólito modular como arquitetura oficial.
O que este ADR decide é a forma concreta dessa modularidade.

## Decisão

Quatro projetos .NET, em camadas, com dependência em uma direção só:

```
Api  ──────►  Application  ──────►  Domain
 │                                    ▲
 └──────►  Infrastructure  ───────────┘
```

- **Domain** — invariantes e primitivos do negócio. Zero dependência: nem de
  outro projeto, nem de pacote de terceiro.
- **Application** — contratos e casos de uso. Depende só de Domain.
- **Infrastructure** — PostgreSQL, EF Core, relógio. Implementa o que
  Application e Domain declaram.
- **Api** — composição HTTP. Não conhece EF Core nem Npgsql.

Módulos de negócio (Transações, Risco, Alertas, Casos…) nascem como **pastas**
dentro dessas camadas, não como projetos novos.

## Por que não um projeto por módulo

Nove projetos de módulo × quatro camadas dariam 36 projetos antes da primeira
regra antifraude existir. O custo aparece em cada build, cada referência e
cada refatoração — e o benefício, isolamento, é obtido com pastas e com os
testes de arquitetura, sem esse preço.

## Por que Infrastructure não pode ser pulada

Confinar EF Core em Infrastructure tem uma consequência concreta que aparece
na Fase 9: o backtest precisa rodar **o mesmo motor de risco** da avaliação
real, com outra fonte de contexto e outro destino de resultado. Isso só é
barato se o motor não souber de onde os dados vêm.

## Consequências

- Uma consulta ao banco a partir de Application exige criar um contrato e
  implementá-lo em Infrastructure. É trabalho extra por consulta.
- Em troca, Domain e Application são testáveis sem banco, sem web e sem
  container — o que mantém a suíte rápida enquanto ela cresce.
- Se a regra atrapalhar de verdade em alguma fase, ela é reaberta por um novo
  ADR, não por remoção silenciosa do teste.

## Verificação

`tests/CentralAntifraude.ArchitectureTests/DependenciasEntreCamadasTests.cs`
checa cada regra em dois níveis: no `.csproj` (pega a referência indevida no
momento em que é adicionada) e no assembly compilado (pega o uso real).

A regra foi validada por mutação: injetar `Npgsql.EntityFrameworkCore.PostgreSQL`
no `.csproj` do Domain faz o teste falhar com mensagem explícita.
