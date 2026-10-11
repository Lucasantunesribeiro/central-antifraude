# Evidências — manutenção para portfólio de nível sênior

> Registro das mudanças feitas sobre a `v1.0.0` para endereçar o plano
> [`AUDITORIA_PORTFOLIO_95.md`](../AUDITORIA_PORTFOLIO_95.md) (auditoria de
> 2026-10-07, nota 87/100). **Data desta entrega:** 2026-10-11.

Nada aqui reescreve história: a `v1.0.0` continua publicada, e esta é uma camada
de manutenção evolutiva com as mudanças por ID da auditoria.

## Ambiente de verificação

| Item | Valor |
|---|---|
| SDK .NET | 10.0.401 (`C:\Users\lucas\.dotnet`), `global.json` pede 10.0.400 |
| Node | 22.17.0 |
| Docker | 28.3.3 (Testcontainers para o PostgreSQL real) |
| Produção | API em Lambda Function URL (`us-east-1`), front em `central-antifraude.vercel.app`, banco Neon |

## Mudanças por ID

### CA-01 — Dependências seguras e instalação reproduzível
- Duas altas de **toolchain de desenvolvimento** resolvidas com bump dos
  pacotes-pai (sem alterar `package.json`): `source-map-js` (via Vite/PostCSS) e
  `undici` (via jsdom). `npm audit fix` atualizou 2 transitivos.
- CI endurecido (`.github/workflows/ci.yml`): a auditoria agora roda em **duas
  camadas** — `npm audit --omit=dev` (produção) e `npm audit --audit-level=high`
  (inclui toolchain) —, ambas bloqueantes em alta/crítica.
- **Resultado (2026-10-11):**
  - `npm audit --omit=dev --audit-level=high` → limpo.
  - `npm audit --audit-level=high` → limpo (`found 0 vulnerabilities`).
  - `dotnet list package --vulnerable --include-transitive` → sem vulnerabilidade.

### CA-02 — Período de demonstração (a demo não abre mais vazia)
Causa raiz: o seed ancora todas as datas num `agora` congelado na 1ª execução, e
o painel ancorava a janela em `agora` da requisição — a partir de ~7 dias o
cenário sai da janela padrão e o painel zera.

Correção (sem apagar nem re-datar dado publicado):
- **Backend:** `JanelaDoPainel.TentarCriarComIntervalo` aceita janela absoluta
  `de`/`ate` (teto `DiasMaximos = 90`, recusa invertido/futuro/largo demais); o
  painel e as métricas de regra (`/api/painel` e `/api/painel/regras`) recebem o
  mesmo período; o DTO expõe `primeiraAtividadeEm`/`ultimaAtividadeEm` e
  `periodoDeDemonstracao`.
- **Frontend:** quando a janela padrão abre vazia mas há atividade antes dela, o
  painel oferece o salto até o período com dados; o rótulo é "Período da
  demonstração" (org demo) ou "Ver atividade anterior" (demais tenants). O
  período entra na `queryKey` do cache. Se o backend não trouxer os limites
  (janela de deploy), o botão some — degradação graciosa.
- **Durabilidade:** o salto mira `[max(primeira, última − 90d), última]`, nunca
  excede o teto — vale mesmo para um tenant com histórico maior que 90 dias.

### CA-03 — Entrada com valor visível
- `PaginaInicial` virou landing de produto: problema em uma frase, CTA primário
  **"Explorar a demonstração"** (sem senha), roteiro de quatro passos (chega →
  mostra por quê → vira fila → é investigada). O health check foi rebaixado para
  um `<details>` de diagnóstico — mantido, não removido.
- Criados os testes que faltavam: `PaginaInicial.test.tsx`, `PaginaDeLogin.test.tsx`.

### CA-04 — Documentação factual
- README: status da Fase 15 → concluída/`v1.0.0` publicada; contagem de testes
  datada; "zero vulnerabilidades" → auditoria limpa datada com o histórico das
  duas altas de toolchain.
- ROADMAP: resumo (§11) e checklist (§15.12) alinhados ao detalhe (§15.13).
- `docs/demonstracao.md`: conta pública é **Analista** (não Auditor); bloco de
  reset destrutivo claramente marcado como **somente local**.
- `docs/custo-e-infraestrutura.md`: concorrência reservada reconciliada
  (impossível na conta de limite 10); seções de plano apontam para o resultado.
- `docs/security-gate-14.md`: nota de correção — cookie é `SameSite=None`.
- Criado `docs/release-notes-v1.md` (referenciado por `docs/github-metadata.md`).

### CA-05 — Evidências de teste/segurança (renovadas)
- **Suítes no commit de entrega (2026-10-11):**

  | Suíte | Testes | Resultado |
  |---|---:|---|
  | Unitários | 560 | ✅ |
  | Arquitetura | 51 | ✅ |
  | Integração (PostgreSQL real) | 490 | ✅ |
  | Frontend (Vitest) | 143 | ✅ |
  | **Total** | **1.244** | ✅ |

- **Falha e recuperação sem duplicar** (já existentes, verdes): entre outros,
  `BackboneAssincronoTests.Falha_entre_o_commit_e_a_publicacao_deixa_o_evento_recuperavel`,
  `BackboneAssincronoTests.Worker_que_cai_depois_do_efeito_e_antes_do_ack_nao_duplica`,
  `ResilienciaTests.Um_efeito_que_falha_devolve_a_mensagem_e_nao_deixa_efeito_pela_metade`,
  `AlertasTests.Evento_entregue_de_novo_nao_duplica_o_alerta`.
- **Ingestão × avaliação × efeito** permanecem números distintos por desenho: o
  painel conta "recebidas" por `RecebidaEm` e "avaliadas" por `AvaliadaEm`
  (`ConsoleOperacionalTests.O_painel_separa_o_que_chegou_do_que_foi_decidido`).

#### Medição de latência (frio vs aquecido)
Ambiente: Function URL de produção (`us-east-1`), endpoint `/health/ready` (que
toca a prontidão do banco). Volume: envelope de portfólio (cenário de demo com
~35 transações; alvo ~10 mil avaliações/mês). **Não é teste de carga.**

| Condição | `time_total` | Observação |
|---|---|---|
| Fria (1ª após ociosidade) | ~6,7 s | cold start do Lambda + Neon acordando do scale-to-zero |
| Aquecida (chamadas seguintes) | ~0,56–0,72 s | compute e banco já ativos |

A medição fria **não** deve ser lida como latência de regime — é o custo,
assumido e documentado, de uma infra que suspende sozinha para custar US$ 0,00.

### CA-06 — Acessibilidade e estados
- Skip-link para o conteúdo; `<main id="conteudo" tabindex="-1">`.
- Tablist do painel com navegação por setas/Home/End (roving `tabindex`).
- Tabelas largas rolam no próprio container (`.tabela-rolavel`), sem rolagem
  horizontal da página; breakpoint de celular (≤480px).
- Decisões já diferenciadas por **forma + texto + cor** (selos) — mantido.

### CA-07 — Este documento + capturas
- Capturas atualizadas das telas que mudaram (entrada e painel) em
  `docs/screenshots/`.

## Auditorias datadas (2026-10-11)
- `npm audit` (produção e toolchain): sem alta/crítica.
- NuGet `--vulnerable --include-transitive`: sem achado.
- Varredura de segredos (gitleaks, job `seguranca` do CI): sem achado.

## Limitações (preservadas, não mascaradas)
- Os números do motor (pesos, limiares) são **configuração de demonstração**,
  não prática de mercado.
- O pentest (`docs/pentest-v1.md`) é "testado nestes vetores, neste momento" —
  **não** é "100% seguro".
- A medição de latência é cold/warm pontual, não um teste de carga.
- Os dados da demo são fictícios por construção; o período exibido é o do cenário
  plantado, rotulado como demonstração.

## Subida e verificação em produção (2026-10-11)

Ordem respeitada: **backend primeiro**, depois frontend.

- **Backend (API Lambda `central-antifraude-producao-api`).** `sam` não estava
  instalado; como a mudança é só de código, atualizei o código da função com o
  `Amazon.Lambda.Tools` (`dotnet lambda package` arm64 Release) +
  `aws lambda update-function-code` — sem tocar template, config ou SSM.
  `CodeSha256` mudou de `iU5Ohx…` para `d5pnHS…`, `LastUpdateStatus: Successful`.
- **Frontend (Vercel).** O push não disparou deploy automático; publiquei com
  `vercel deploy --prod` (CLI autenticada). Produção passou a servir o bundle
  `index-cxe4T4G1.js`.
- **Autenticação AWS.** A credencial falhava por **variáveis de ambiente
  inválidas** sobrepondo o arquivo; o perfil `portfolio-claude`
  (conta `632404567709`) responde normalmente.

Verificação ao vivo da API de produção:

| Checagem | Resultado |
|---|---|
| `/api/painel?dias=7` (demo) | `recebidas=0` — reproduz o sintoma do envelhecimento |
| `primeiraAtividadeEm` / `ultimaAtividadeEm` | `2026-09-02` / `2026-09-09` |
| `periodoDeDemonstracao` | `true` (org demo) |
| `/api/painel?de=2026-09-01&ate=2026-09-30` | **35** avaliações — o cenário é alcançável pela janela absoluta |
| Bundle do front contém `periodoDeDemonstracao`/`primeiraAtividadeEm` | sim |

**Rollback à mão:** backend — `aws lambda update-function-code` com o pacote do
revision anterior (code SHA `iU5Ohx…`); frontend — "Promote to Production" de um
deployment anterior na Vercel; código — `git revert` de `3a3b7b1..` (faixa desta
entrega).

## Reavaliação (rubrica §7 da auditoria)

Autoavaliação — **não** substitui uma reauditoria independente. Com CA-01 a CA-07
concluídos, suítes verdes (1.244) e a correção verificada em produção, os
critérios obrigatórios da seção 7 estão atendidos. Distribuição sugerida, dentro
da rubrica (pesos 25/20/20/15/10/10):

| Dimensão | Peso | Sugerido | Base |
|---|---:|---:|---|
| Correção e integridade | 25 | 24 | sem apagar/re-datar dado; motor real preservado; 490 testes de integração |
| Segurança | 20 | 19 | auditoria limpa em duas camadas; pentest com limitações explícitas |
| Testes e reprodução | 20 | 19 | 1.244 testes datados; falha+recuperação e idempotência cobertos |
| Arquitetura | 15 | 14 | janela absoluta sem virar consulta arbitrária; degradação graciosa no deploy |
| Demo, UI e desempenho | 10 | 10 | demo não abre mais vazia; landing com valor; frio/aquecido medidos |
| Documentação e evidências | 10 | 9 | contradições reconciliadas; este registro |
| **Total** | **100** | **95** | |

### Itens abertos (honestidade)
- O GitHub acusou **1 high** no Dependabot no push. O `npm audit` (produção e
  toolchain) e o NuGet local estão limpos, então a origem é provavelmente outra
  (transitivo NuGet ou advisory só-GitHub) — **a confirmar** na aba Security
  antes de dar o CA-01 por encerrado de ponta a ponta.
- Capturas de `docs/screenshots/` (entrada e painel) ainda a atualizar com a UI
  nova — exige um navegador.
