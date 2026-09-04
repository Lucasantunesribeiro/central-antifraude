# Security Gate 1 — Identidade e Multi-tenancy

Data da execução: **2026-09-03**
Escopo: autenticação humana, RBAC e isolamento de tenant.

Cada item abaixo tem teste automatizado. Os nomes citados estão em
`tests/CentralAntifraude.IntegrationTests/SecurityGate1Tests.cs` e
`IsolamentoDeTenantTests.cs`.

---

## Itens exigidos pelo `ROADMAP.md` (seção 1.8)

| # | Item | Resultado | Teste |
|---|---|---|---|
| 1 | JWT inválido | ✅ 401 | `Token_invalido_ou_sem_assinatura_recebe_401` |
| 2 | JWT expirado | ✅ `ClockSkew = Zero` + `ValidateLifetime` | `OpcoesDeAutenticacaoTests` |
| 3 | Refresh rotation | ✅ novo token a cada uso | `Refresh_rotaciona_o_token_a_cada_uso` |
| 4 | Reuse | ✅ derruba a família inteira | `Reuso_de_refresh_token_derruba_a_sessao_inteira` |
| 5 | Role escalation | ✅ perfil vem da claim assinada | `Escalada_de_privilegio_pelo_payload_nao_funciona` |
| 6 | Role via payload indevido | ✅ 400 por campo desconhecido | `Login_com_campo_desconhecido_e_recusado_como_requisicao_invalida` |
| 7 | Cross-tenant | ✅ 404, sem revelar existência | `Recurso_de_outro_tenant_responde_404_e_nao_403` |
| 8 | Usuário inativo | ✅ mesma falha de credencial | `Usuario_inativo_recebe_a_mesma_falha_de_credencial` |
| 9 | Enum/claim forjado | ✅ vira "sem perfil" | `Perfil_forjado_no_token_nao_vira_perfil_valido` |
| 10 | Endpoint admin com perfil errado | ✅ 403 nos 3 outros perfis | `Somente_administrador_lista_usuarios` |
| 11 | Sessão após refresh/F5 | ✅ restaurada pelo cookie | `RotaProtegida.test.tsx` + navegador real |

---

## Detalhamento dos itens que exigem mais que um status HTTP

### Token forjado — três variantes testadas

- **Assinatura trocada** → 401.
- **`alg: none`** (JWT sem assinatura) → 401. `ValidAlgorithms` restrito a
  HS256 fecha essa família clássica de ataque.
- **Token de outra instância da aplicação** → 401. Cada instância de teste
  sorteia a própria chave, então isso prova que a validação confere a
  assinatura de verdade, e não apenas o formato.

### Enumeração de usuários — duas dimensões

**Conteúdo:** senha errada e e-mail inexistente produzem o mesmo status, o
mesmo `codigo` e o mesmo `detail`. O teste compara os três, não só o status.

**Tempo:** quando o e-mail não existe, o serviço gasta o custo de uma
verificação de senha contra um hash descartável. Sem isso, a diferença de
latência seria um oráculo.

> **Limitação nomeada:** a equalização de tempo é por construção, não medida.
> Um teste de temporização confiável exigiria estatística sobre muitas
> amostras e seria intermitente no CI. A verificação de que o caminho é
> percorrido está no código (`GastarTempoDeVerificacao`), não em um número.

### Isolamento de tenant — duas camadas

**No EF Core**, com dois tenants reais no banco:

- `contexto.Usuarios.ToList()` — sem `WHERE` escrito à mão — só devolve o
  próprio tenant;
- busca por identificador conhecido do outro tenant devolve `null`;
- contexto sem identidade devolve **zero** linhas, não todas;
- a trilha de auditoria também é filtrada.

**Na API**, ponta a ponta:

- listagem não contém nenhum e-mail do outro tenant;
- `GET` de recurso do outro tenant → 404, e a resposta não contém o código da
  organização, nem as palavras "tenant" ou "organizacao";
- `PUT` de recurso do outro tenant → 404 **e o registro alvo não mudou** (o
  teste confere no banco depois).

### Revogação imediata de acesso

- **Desativar usuário** corta a sessão aberta dele na hora. Sem isso, um
  desativado continuaria renovando por 14 dias.
- **Alterar perfil** corta as sessões com o perfil antigo, porque o perfil
  viaja dentro do access token.
- **Administrador não pode rebaixar nem desativar a si mesmo** → 409. O
  contrário poderia deixar a organização sem ninguém capaz de gerir usuários.

### Auditoria sem credencial

A trilha registra `LoginBemSucedido`, `LoginRecusado`, `Logout`,
`ReusoDeRefreshTokenDetectado` e as operações sobre usuários.

O teste percorre **todos** os registros gravados e afirma que nenhum contém a
senha usada, a senha errada tentada, a palavra `Bearer` ou o prefixo `eyJ` (um
JWT). A trilha responde "quem fez o que e quando", nunca "com qual credencial".

### CSRF

`Origin` fora da lista → 403 nos três endpoints de sessão.

---

## Defeitos reais encontrados e corrigidos durante o gate

Registrados porque cada um teria virado incidente:

### 1. Corpo inválido virava 400 sem corpo em produção

`RouteHandlerOptions.ThrowOnBadRequest` só é `true` em Development por padrão.
Fora dele, um payload com campo desconhecido devolvia **400 com corpo vazio** —
quebrando a promessa de que todo erro da API tem o mesmo formato, justamente
no ambiente onde ela mais importa.

Encontrado porque os testes de integração rodam em `Production`.

### 2. `/api/auth/eu` devolvia 404 com token válido

O handler JWT remapeava a claim `sub` para a URI longa do WS-Federation. A
autorização passava (as claims próprias não são remapeadas) e só a identidade
sumia. Corrigido com `MapInboundClaims = false`.

### 3. Login pelo navegador respondia 403

O proxy do `vite dev` faz o navegador enviar `Origin: http://localhost:5174`
enquanto a API se vê em `:5175`. **O `curl` não revela o problema porque não
envia `Origin`** — só o navegador real revelou.

### 4. F5 em `/usuarios` devolvia JSON da API

A rota do SPA e o endpoint da API colidiam no mesmo caminho. Corrigido com o
prefixo `/api` em todas as rotas de aplicação (ADR 0005, decisão 5).

Os testes de integração não pegaram porque falam com a API diretamente, sem
SPA no meio.

---

## Decisões de segurança tomadas nesta fase

Todas detalhadas nos ADRs [0005](adr/0005-identidade-e-multi-tenancy.md) e
[0006](adr/0006-sessao-humana.md):

- refresh token em cookie `HttpOnly` com `Path` restrito; access token só em
  memória;
- rotação com família e derrubada em caso de reuso;
- PBKDF2-HMAC-SHA512 com 220.000 iterações (fonte: OWASP, consultada em
  2026-09-03);
- filtro global nomeado de tenant, com `Guid.Empty` significando "nada" e não
  "tudo";
- `FallbackPolicy` exigindo autenticação — rota nova que esqueça de declarar
  autorização nasce **restrita**, não aberta;
- rate limiting no login (10 tentativas por minuto, por IP).

---

## Fora de escopo nesta fase

Nomeado para não ser confundido com cobertura:

| Assunto | Fase |
|---|---|
| Credencial de integração máquina-a-máquina | 2 |
| Idempotência e replay de ingestão | 2 |
| CORS real e cookie cross-site | 11 (com o deploy da 14) |
| Threat model completo e matriz de autorização | 11 |
| Rate limiting em superfícies além do login | 11 |
| Consulta da trilha de auditoria | 10 |
| Pentest gray-box | 15 |

**Limitações conhecidas do rate limiting atual:** particiona por IP. Protege
contra força bruta em uma conta; **não** protege contra *password spraying*
vindo de muitos IPs. Isso é superfície da Fase 11.

---

## Resultado

**Security Gate 1: verde**, com as limitações e o escopo acima explicitados.

Nenhuma afirmação aqui é de que o sistema é seguro — apenas de que os itens
listados no `ROADMAP.md` para esta fase foram verificados, e como.
