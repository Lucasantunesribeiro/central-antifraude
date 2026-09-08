# Metadata do GitHub — a aplicar quando o push for autorizado

Estes valores foram preparados na Fase 15 (§15.10). Nenhum foi aplicado: exigem
um repositório remoto, que só existe depois do `git push` — não autorizado.

Quando o repositório existir, aplicar com `gh` (não declara feature inexistente,
§15.10):

```bash
gh repo edit \
  --description "Plataforma B2B de antifraude: avalia risco de pagamentos digitais de forma determinística e explicável, decide na hora e processa os efeitos de forma assíncrona, idempotente e auditável." \
  --homepage "https://central-antifraude.vercel.app" \
  --add-topic fraud-detection \
  --add-topic dotnet \
  --add-topic aspnetcore \
  --add-topic postgresql \
  --add-topic serverless \
  --add-topic aws-lambda \
  --add-topic event-driven \
  --add-topic idempotency \
  --add-topic react \
  --add-topic typescript \
  --add-topic portfolio
```

## Description (≤ 350 caracteres, sem feature inexistente)

> Plataforma B2B de antifraude: avalia risco de pagamentos digitais de forma
> determinística e explicável, decide na hora e processa os efeitos de forma
> assíncrona, idempotente e auditável.

## Website

`https://central-antifraude.vercel.app`

## Topics

`fraud-detection` · `dotnet` · `aspnetcore` · `postgresql` · `serverless` ·
`aws-lambda` · `event-driven` · `idempotency` · `react` · `typescript` ·
`portfolio`

## Social preview

Sugestão: `docs/screenshots/03-transacao-sinais-explicaveis.jpg` — a tela "Por
que esta transação recebeu 75" é a que melhor comunica o diferencial
(explicabilidade determinística) numa imagem só.

## Release v1.0.0 — a criar após o push (§15.11)

```bash
git tag -a v1.0.0 -m "Central Antifraude v1.0.0 — Portfolio Release"
git push origin v1.0.0
gh release create v1.0.0 \
  --title "Central Antifraude v1.0.0 — Portfolio Release" \
  --notes-file docs/release-notes-v1.md
```

As notas de release podem ser montadas a partir dos commits das fases 0–15.
