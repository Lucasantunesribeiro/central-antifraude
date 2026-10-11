# Central Antifraude — Release v1.0.0

_Publicada em 2026-09-09._

A **Central Antifraude** recebe transações de pagamentos digitais, avalia risco de forma **determinística e explicável**, retorna uma decisão imediata (`Permitir` / `Revisar` / `Bloquear`) e processa os efeitos operacionais de forma **assíncrona, idempotente e auditável**.

Esta é a primeira release estável — o encerramento das 16 fases (0 a 15) do projeto.

## Demonstração ao vivo

- **Aplicação:** https://central-antifraude.vercel.app — há um botão **"Explorar a demonstração"** que entra sem senha, como Analista de Fraude.
- **Vídeo e screenshots:** no [README](README.md#demonstração-ao-vivo).
- Custo de nuvem esperado para uso de portfólio: **US$ 0,00** (Neon, Lambda e SQS que suspendem quando ociosos).

## O que a v1.0.0 entrega

- **Motor de risco determinístico e explicável** — cada decisão registra os sinais, os pontos de cada regra, e as versões de perfil e de motor usadas; uma avaliação histórica continua explicável mesmo depois de as regras mudarem.
- **Regras versionadas e imutáveis**, com **backtest** sobre histórico usando o mesmo motor da produção.
- **Ingestão idempotente e concorrente** — chave de idempotência, `SERIALIZABLE` no caminho crítico com retry próprio, e proteção por constraint única no banco.
- **Transactional Outbox + consumidores idempotentes** sobre SQS Standard (at-least-once), com DLQ.
- **Investigação humana auditável** — alertas, casos, timeline append-only, e o **falso positivo obrigatório** (motor `Revisar` → investigação `Legítima`).
- **Segurança aplicacional** — RBAC no backend, isolamento multi-tenant, rotação de refresh token com detecção de reuso, rate limiting, JSON estrito e defesa de mass assignment. Pentest gray-box com 19 vetores defendidos e 0 achados de alta severidade.
- **Observabilidade** — Correlation ID ponta a ponta, logs estruturados e métricas de baixa cardinalidade.
- **Console operacional** em React, redesenhado no estilo light fintech, com foco em legibilidade e uma ação principal por tela.

## Qualidade

- **1.215+ testes** (unitários, domínio, integração com PostgreSQL real via Testcontainers, autorização, multi-tenancy, concorrência, idempotência, workers e frontend), CI verde.
- Build backend em `Release` com **0 avisos** e `TreatWarningsAsErrors`.

## Limitações

Projeto de portfólio. Dados de demonstração são fictícios por construção; pesos e limiares são **configuração de demonstração**, não prática de mercado. A Central Antifraude **não processa dinheiro** — recomenda risco, não autoriza pagamento.

## Stack

.NET 10 · ASP.NET Core · EF Core · PostgreSQL (Neon) · AWS Lambda + SQS · React + TypeScript + Vite · Vercel.
