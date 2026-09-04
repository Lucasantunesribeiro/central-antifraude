# ADR 0006 — Sessão humana: tokens, senha e CSRF

- **Status:** aceito
- **Data:** 2026-09-03
- **Fase:** 1 — Identidade, Organizações e Multi-tenancy

## Contexto

O `CLAUDE.md` (seção 49) exige access token curto, refresh token com rotação,
detecção de reuso e armazenamento seguro. O `ROADMAP` (seção 1.3) acrescenta:
**não usar localStorage para o refresh token**, e respeitar o deploy
cross-origin planejado (Vercel + Lambda, Fase 14).

---

## Decisão 1 — Onde cada token vive

| Token | Formato | Onde fica | Vida |
|---|---|---|---|
| Access | JWT HS256 | **memória do JavaScript** | 15 min |
| Refresh | 256 bits aleatórios, opaco | **cookie HttpOnly**, `Path=/api/auth` | 14 dias |

**Por que o access token em memória.** Ele é curto e reemitível. Guardá-lo em
`localStorage` daria a qualquer XSS uma credencial pronta; numa variável de
módulo, o XSS ainda faz estrago, mas não encontra a credencial parada
esperando.

**Por que o refresh em cookie HttpOnly.** É a credencial de longa duração — a
que realmente vale roubar. `HttpOnly` a torna invisível ao JavaScript. O
efeito colateral é o que faz o F5 funcionar: o frontend não tem token nenhum
ao carregar a página, pergunta ao servidor se o cookie ainda vale, e recebe a
sessão de volta.

**`Path=/api/auth`.** O cookie não acompanha as chamadas normais da API. Não
aparece em log de proxy de requisição comum, nem é exposto por um endpoint que
reflita cabeçalhos.

**Por que o refresh é opaco e não um JWT.** Ele não precisa carregar
informação nenhuma. Um valor sem significado não pode ser lido nem forjado —
só conferido contra a linha do banco. E é o que permite revogá-lo de verdade,
coisa que um JWT auto-contido não permite.

**Só o hash é persistido** (SHA-256, hexadecimal). Um vazamento do banco não
entrega sessão a ninguém.

> **Por que SHA-256 puro aqui, e PBKDF2 na senha.** Alongamento de chave existe
> para compensar a baixa entropia de senhas escolhidas por pessoas. Um valor de
> 256 bits sorteado não tem esse problema: não há dicionário, não há palpite.
> PBKDF2 custaria centenas de milissegundos por renovação sem aumentar a
> segurança em nada.

---

## Decisão 2 — Rotação com família e detecção de reuso

Cada uso do refresh token o troca por outro. Todos os descendentes de um mesmo
login compartilham um `FamiliaId`.

Se um token **já usado** for apresentado de novo, há duas cópias em circulação
— a legítima e a roubada — e não há como saber qual é qual. A única resposta
segura é **derrubar a família inteira** e exigir novo login. O evento vira
registro de auditoria (`ReusoDeRefreshTokenDetectado`).

Não há entidade `Sessao` separada: a família já é a sessão, e uma tabela a
mais não acrescentaria capacidade nenhuma.

O acesso também é cortado na hora quando um administrador **desativa** o
usuário ou **muda seu perfil** — o perfil viaja dentro do access token, e sem
revogar, o usuário rebaixado continuaria agindo com o perfil antigo até o
token expirar.

---

## Decisão 3 — Hash de senha: PBKDF2-HMAC-SHA512, 220.000 iterações

Usa o `PasswordHasher<T>` do ASP.NET Core (formato V3) em vez de implementação
própria. Ele já resolve, com código revisado, três coisas fáceis de errar
sozinho: salt aleatório por senha, comparação em tempo constante, e um byte de
versão no início do hash — que permite subir o custo no futuro sem invalidar
as senhas existentes.

**O número tem fonte.** OWASP Password Storage Cheat Sheet, consultada em
2026-09-03: PBKDF2-HMAC-SHA512 com **220.000** iterações. O padrão da
biblioteca é 100.000. Este não é um "padrão de mercado" inventado — é uma
escolha do projeto, com referência, e um teste unitário trava o valor para que
baixá-lo seja uma decisão consciente.

**A mesma folha coloca Argon2id em primeiro lugar.** Ele exigiria dependência
de terceiro no projeto. Fica registrado como caminho de evolução conhecido, e
não como pendência de segurança: PBKDF2 nos parâmetros recomendados é
explicitamente aceitável na mesma fonte.

**Regravação automática.** Quando o custo subir, o login detecta o hash antigo
e o regrava — é o único momento em que a senha em claro existe. Ninguém
precisa trocar de senha.

---

## Decisão 4 — CSRF por verificação de `Origin`

Os três endpoints que dependem do cookie (`login`, `refresh`, `logout`)
recusam requisição cujo `Origin` não esteja na lista de origens confiáveis.

O cabeçalho `Origin` é preenchido pelo navegador e **não pode ser alterado por
JavaScript de outra página**. É a defesa que funciona tanto em mesma origem
quanto no deploy cross-site planejado.

**Requisição sem `Origin` é aceita.** Clientes que não são navegador (curl,
teste de integração) não enviam o cabeçalho — e também não carregam cookie de
terceiros automaticamente, que é a premissa do ataque. Recusar por ausência
bloquearia clientes legítimos sem fechar nenhum vetor.

`SameSite=Lax` e `Secure` conforme a conexão. O deploy cross-site vai exigir
`SameSite=None` com CORS credenciado — decisão da Fase 11, junto do resto do
desenho de browser security. A verificação de `Origin` vale nos dois modelos.

> **Armadilha encontrada em desenvolvimento.** O proxy do `vite dev` faz o
> navegador enviar `Origin: http://localhost:5174` enquanto a API se vê em
> `:5175`. A origem do frontend precisa estar declarada em
> `appsettings.Development.json` — sem isso o login responde 403 sem
> explicação óbvia. O `curl` não revela o problema porque não envia `Origin`.

---

## Decisão 5 — Nenhuma resposta revela se um e-mail existe

Senha errada, usuário inexistente, usuário desativado e organização desativada
produzem **exatamente** a mesma falha: `401`, código `credenciais_invalidas`,
mesma mensagem.

E o **tempo de resposta** também não revela: quando o e-mail não existe, o
serviço ainda assim gasta o custo de uma verificação de senha contra um hash
descartável. Sem isso, a diferença de latência entre "existe" e "não existe"
seria um oráculo de enumeração de usuários.

---

## Decisão 6 — `MapInboundClaims = false`

O handler JWT do ASP.NET, por padrão, renomeia claims curtas para as URIs
longas do WS-Federation: `sub` vira
`http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier`.

O sintoma era traiçoeiro: a autorização passava (as claims próprias do projeto
não são remapeadas) e apenas a identidade sumia — virando **404** em vez de
401 no `/api/auth/eu`. Desligado, os nomes de claim chegam como foram
emitidos.

---

## Consequências

- Uma aba nova não herda a sessão instantaneamente: ela faz um `refresh`
  primeiro. É um round-trip a mais no carregamento, e é o preço de não guardar
  credencial no navegador.
- Rotação a cada renovação significa uma escrita no banco a cada 14 minutos
  por usuário ativo. Com o envelope de portfólio (`CLAUDE.md` seção 77) isso é
  irrelevante; num volume maior mereceria medição.
- Duas abas renovando ao mesmo tempo podem disparar a detecção de reuso e
  derrubar a sessão. Não observado nos testes, porque a renovação acontece
  perto da expiração e não simultaneamente — mas é a hipótese a investigar
  primeiro se aparecer relato de "fui deslogado do nada". Registrado como
  débito técnico da fase.
