# Modelo de ameaças — Central Antifraude

Data: **2026-09-07** · Fase 11 (ROADMAP 11.2)

Este documento descreve os fluxos do produto, o que pode dar errado em cada um
e o que já está no lugar. É um mapa de decisões, não uma lista de boas
práticas: cada mitigação abaixo aponta para código e teste.

**O que este documento não é.** Não é certificação, não é pentest e não afirma
conformidade com norma nenhuma. A Central Antifraude é um projeto de portfólio
com dados fictícios.

---

## Os ativos

Em ordem de dano se comprometidos:

| Ativo | Por que importa |
|---|---|
| **Isolamento entre organizações** | um cliente ver o movimento de outro é o dano que não tem conserto |
| **Credencial de integração** | quem a tem envia transação como se fosse o cliente |
| **Sessão humana** | quem a tem opera como a pessoa, incluindo publicar regra |
| **Configuração do motor** | uma regra publicada alcança toda transação que entrar depois |
| **Trilha de auditoria** | é o único registro de quem fez o quê; alterável, não prova nada |
| **Explicabilidade histórica** | uma avaliação que muda depois destrói a resposta a "por quê" |

**O que o produto deliberadamente não guarda** — e portanto não pode vazar:
PAN, CVV, senha bancária, endereço IP bruto, dados completos de cartão. O
instrumento é referência tokenizada; o IP é HMAC (`CLAUDE.md` seções 56 a 58).

---

## Fluxo 1 — Navegador → API

```text
navegador  ──HTTPS──►  API
   ▲                    │
   └── cookie HttpOnly ─┘
```

| Ameaça | Mitigação | Onde |
|---|---|---|
| Roubo do refresh token por XSS | cookie `HttpOnly`, `Secure`, `SameSite`; access token só em memória | `SessaoHttp` |
| CSRF | verificação de `Origin` contra lista declarada, nas rotas que dependem do cookie | `GarantirOrigemConfiavel` |
| Origem cruzada não autorizada | CORS com lista explícita, sem curinga, `AllowCredentials` só para as origens declaradas | `PoliticaDeCors` |
| XSS refletido pela API | JSON puro; a tela renderiza texto, nunca HTML; texto com marcação é **recusado**, nunca limpo | `TextoDeUsuario` |
| Resposta embutida em iframe / vazamento de caminho no `Referer` | `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `nosniff` | `MiddlewareDeCabecalhosDeSeguranca` |
| Sessão viva depois de comprometida | access token de 15 min sem tolerância de relógio; rotação de refresh com detecção de reuso derruba a família | `ServicoDeAutenticacao` |
| Força bruta de senha | limite por IP no login | `LimiteDeLogin` |
| Adivinhação de refresh token | limite por IP no refresh | `LimiteDeRefresh` |

**Risco aceito:** o limite de login é por IP e não protege contra *password
spraying* distribuído. Rate limiting distribuído depende de estado
compartilhado, que a arquitetura não tem — e não terá sem problema concreto.

---

## Fluxo 2 — Integração → API

```text
sistema do cliente  ──ApiKey──►  /api/ingestao/transacoes
```

| Ameaça | Mitigação | Onde |
|---|---|---|
| Credencial vazada em repouso | só o hash é persistido; o segredo aparece uma vez, na emissão | `SegurancaDeIntegracao` |
| Credencial vazada em log | o handler registra o motivo da recusa, nunca a chave | `ManipuladorDeAutenticacaoDeIntegracao` |
| Escolher o tenant pelo payload | o tenant vem da credencial; `tenantId` no corpo é campo desconhecido → `400` | Gate 2 |
| Replay de transação | idempotência em três camadas: consulta, restrição única, nova consulta | ADR 0007 |
| Payload conflitante com a mesma chave | fingerprint canônico do conteúdo → `409` | ADR 0007 |
| Corpo gigante | 8 KB no endpoint, 64 KB global | `EndpointsDeIngestao` |
| Enxurrada de requisições | limite por credencial, e não por IP — instâncias do integrador saem de IPs diferentes | `LimiteDeIngestao` |
| Sessão humana usada como integração, e vice-versa | esquemas de autenticação separados; cada um responde `401` no lugar do outro | Gate 2 |
| PAN enviado por engano | detector de Luhn recusa a ingestão | Gate 2 |

**Sem HMAC de request.** Para cliente→servidor sobre TLS com credencial
própria e idempotência, assinar o payload não resolve problema que já não
esteja resolvido (`CLAUDE.md` seção 51). Passa a ser relevante em webhook de
saída, que não existe.

---

## Fluxo 3 — API → PostgreSQL

| Ameaça | Mitigação | Onde |
|---|---|---|
| SQL injection | EF Core parametriza; nenhum SQL é concatenado com entrada | Gate 10 |
| Curinga de `LIKE` ampliando a busca | `%`, `_` e `\` escapados antes do padrão | `FiltroDeTransacoes.PadraoDeBusca` |
| Ordenação por coluna arbitrária | vocabulário fechado; o texto do cliente nunca chega à consulta | `ParametrosDeOrdenacao` |
| Enum forjado por número | comparação por **nome**; `decisao=2` é recusado | `VocabularioFechado` |
| Consulta escrita sem filtro de tenant | filtro global nomeado no `DbContext`, em toda entidade com organização | `CentralAntifraudeDbContext` |
| Paginação abusiva | página e tamanho validados; recusa, não corrige | `ParametrosDePaginacao` |
| Perda de decisão por concorrência | `SERIALIZABLE` na ingestão, retry deliberado da operação inteira | ADR 0009 |

**A armadilha registrada:** SQL cru **não** escapa do filtro global — o EF o
compõe por cima. Onde o filtro precisa sair (despachante, worker, backtest),
ele é desligado **pelo nome** e a organização entra explicitamente na cláusula.

---

## Fluxo 4 — API → Outbox → fila → worker

```text
transação + avaliação + evento   (mesma transação)
        │
        ▼  despachante
     fila (operacional | backtests)
        │
        ▼  worker
     efeito + marca na Inbox   (mesmo commit)
```

| Ameaça | Mitigação | Onde |
|---|---|---|
| Evento publicado sobre estado que não existe | Outbox: evento e mudança no mesmo commit | ADR 0009 |
| Evento perdido | publica antes de marcar; duplicata é aceitável, perda não | ADR 0010 |
| Efeito duplicado por reentrega | Inbox com restrição única, no mesmo commit do efeito | ADR 0010 |
| Mensagem forjada declarando outro tenant | o tenant do envelope é conferido contra o banco | Gates 5 e 9 |
| Mensagem envenenada em loop | redrive para DLQ após N entregas; nada é apagado sem análise | Gate 5 |
| Backtest atrasando alerta | filas separadas por tipo de evento | ADR 0014 |
| Dois workers concluindo o mesmo backtest | token de versão na execução | Gate 9 |

**O que o worker nunca faz:** confiar no que chega. O envelope é dado, nunca
instrução — tipo, versão e tenant são conferidos antes de qualquer efeito.

---

## Fluxo 5 — Supervisor → regras → motor

| Ameaça | Mitigação | Onde |
|---|---|---|
| Perfil errado publicando regra | política `Supervisor` na rota **e** segunda checagem no serviço | Gate 8 |
| Configuração arbitrária virando comportamento | catálogo fechado e tipado; a API recebe números nomeados, nunca expressão | ADR 0008 |
| Edição do que já foi publicado | versão publicada sem setter, sem método e sem rota — teste de arquitetura vigia | `ImutabilidadeDoPublicadoTests` |
| Duas publicações simultâneas | token de versão + índices únicos de numeração | ADR 0013 |
| Backtest como porta lateral para o motor | o candidato é montado pelo servidor a partir do rascunho gravado | ADR 0014 |
| Backtest contaminando a operação | não altera avaliação, alerta, caso nem transação — contagens conferidas no banco | Gate 9 |

---

## Fluxo 6 — Auditoria

| Ameaça | Mitigação | Onde |
|---|---|---|
| Trilha alterada por quem é auditado | leitura só para Administrador e Auditor; **nenhuma rota** escreve, altera ou apaga | Gate 10 |
| Segredo na trilha | nunca recebe senha, hash, token nem payload; verificado na saída com 8 palavras proibidas | Gate 10 |
| Trilha de outro tenant | filtro global; conjuntos de identificadores disjuntos entre organizações | Gate 10 |
| Confirmação de existência via filtro | `entidadeId` de outro tenant devolve zero, e não erro distinto | Gate 11 |

---

## Fluxo 7 — Segredos e configuração

| Ameaça | Mitigação | Onde |
|---|---|---|
| Segredo no repositório | `gitleaks` no CI, em histórico e árvore de trabalho | Gate 0 |
| Segredo em `appsettings` | nenhuma chave versionada; falha fechada se a configuração faltar | `InjecaoDeDependencia` |
| Segredo em log | contrato de log estruturado sem payload, sem token, sem credencial | `CLAUDE.md` seção 70 |
| Dependência vulnerável | `dotnet list package --vulnerable` e `npm audit` a cada fase | Gates 0 a 11 |
| Configuração inválida virando erro em produção | toda opção valida na inicialização; o processo não sobe | opções de cada área |

---

## Ameaças fora de escopo, nomeadas

| Ameaça | Por que fora |
|---|---|
| SSRF | o backend não faz requisição para URL configurável. Se um webhook de saída entrar, o `CLAUDE.md` seção 60 já define o que fazer |
| Prompt injection | não há IA no produto. O `CLAUDE.md` seção 61 define as regras se houver |
| Ataque de canal lateral por tempo no login | a equalização é por construção, não medida — um teste confiável exigiria estatística e seria intermitente no CI |
| Comprometimento do host / supply chain do runtime | fora do que a aplicação controla; mitigação pertence ao deploy (Fase 14) |
| DDoS volumétrico | pertence à borda da nuvem, não à aplicação |

---

## O que muda na Fase 14

O deploy cross-site (frontend na Vercel, API em Function URL) ativa três coisas
que hoje estão configuradas e não exercitadas em produção:

1. **CORS** com a lista real de origens;
2. **cookie cross-site**, que exige `SameSite=None; Secure`;
3. **TLS obrigatório**, que hoje é responsabilidade do ambiente.

Os três já têm teste (Gate 11) contra a configuração que será usada — a ordem
foi deliberada, para não descobrir cross-origin depois do deploy.
