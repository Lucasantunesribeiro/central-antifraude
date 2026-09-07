# Matriz de autorização

Data: **2026-09-07** · Fase 11 (ROADMAP 11.6)

Quem alcança o quê, em um lugar só.

**Este documento não é a fonte da verdade — ele é derivado dela.** A fonte é a
tabela declarada em
`tests/CentralAntifraude.IntegrationTests/MatrizDeAutorizacaoTests.cs`, e o
teste compara essa tabela com as rotas que a aplicação **realmente expõe**,
lidas do roteamento. Uma rota nova sem entrada quebra a build; uma entrada sem
rota, também.

---

## Os perfis

| Perfil | O que faz |
|---|---|
| **Administrador** | usuários, integrações, credenciais — e alcança tudo o que os outros alcançam |
| **SupervisorDeFraude** | gere regras, publica versões, executa backtests |
| **AnalistaDeFraude** | trabalha alertas e casos |
| **Auditor** | lê decisões, histórico e a trilha. Não age |

## As políticas

| Política | Perfis |
|---|---|
| `perfil:administrador` | Administrador |
| `perfil:supervisor` | Administrador, Supervisor |
| `perfil:operacao` | Administrador, Supervisor, Analista |
| `perfil:auditoria` | Administrador, Auditor |
| `perfil:qualquer` | os quatro |
| `integracao:ingestao` | esquema `ApiKey`, nunca sessão humana |

**O Auditor fica fora de `perfil:operacao` de propósito.** Agir sobre alerta ou
caso é ação operacional; o perfil dele é de leitura (ROADMAP 1.9).

**O Supervisor fica fora de `perfil:auditoria` de propósito.** A trilha é um
controle *sobre* o que ele faz — publicar regra. Dar a quem é auditado o poder
de varrer o próprio rastro enfraquece o único registro que responde "quem fez o
quê e quando" (`CLAUDE.md` seções 8.3 e 67).

---

## Rotas anônimas

Cinco, e cada uma tem razão:

| Rota | Por quê |
|---|---|
| `GET /health/live` | um orquestrador que precisasse de token para saber se o processo está vivo não teria como obtê-lo |
| `GET /health/ready` | idem |
| `POST /api/auth/login` | a identidade é o **resultado**; a prova é a senha. Tem limite por IP |
| `POST /api/auth/refresh` | a prova é o cookie. Tem limite por IP |
| `POST /api/auth/logout` | encerrar sessão com token expirado precisa funcionar |

Qualquer outra rota anônima quebra o teste
`Nenhuma_rota_de_api_e_anonima_alem_das_de_sessao`. A política de *fallback*
já exige autenticação em quem esquece de declarar — mas `AllowAnonymous` é
escolha explícita, e escolha explícita precisa de justificativa explícita.

---

## Matriz

### Sessão

| Verbo | Rota | Política |
|---|---|---|
| POST | `/api/auth/login` | anônima |
| POST | `/api/auth/refresh` | anônima |
| POST | `/api/auth/logout` | anônima |
| GET | `/api/auth/eu` | `perfil:qualquer` |

### Ingestão

| Verbo | Rota | Política |
|---|---|---|
| POST | `/api/ingestao/transacoes` | `integracao:ingestao` |

### Administração

| Verbo | Rota | Política |
|---|---|---|
| GET | `/api/usuarios` | `perfil:administrador` |
| GET | `/api/usuarios/{id}` | `perfil:administrador` |
| POST | `/api/usuarios` | `perfil:administrador` |
| PUT | `/api/usuarios/{id}/perfil` | `perfil:administrador` |
| PUT | `/api/usuarios/{id}/nome` | `perfil:administrador` |
| PUT | `/api/usuarios/{id}/ativacao` | `perfil:administrador` |
| PUT | `/api/usuarios/{id}/senha` | `perfil:administrador` |
| GET | `/api/integracoes` | `perfil:administrador` |
| GET | `/api/integracoes/{id}` | `perfil:administrador` |
| POST | `/api/integracoes` | `perfil:administrador` |
| PUT | `/api/integracoes/{id}/nome` | `perfil:administrador` |
| PUT | `/api/integracoes/{id}/ativacao` | `perfil:administrador` |
| POST | `/api/integracoes/{id}/credenciais` | `perfil:administrador` |
| DELETE | `/api/integracoes/{id}/credenciais/{credencialId}` | `perfil:administrador` |

### Leitura operacional

| Verbo | Rota | Política |
|---|---|---|
| GET | `/api/transacoes` | `perfil:qualquer` |
| GET | `/api/transacoes/{id}` | `perfil:qualquer` |
| GET | `/api/regras` | `perfil:qualquer` |
| GET | `/api/regras/perfil` | `perfil:qualquer` |
| GET | `/api/alertas` | `perfil:qualquer` |
| GET | `/api/casos` | `perfil:qualquer` |
| GET | `/api/casos/{id}` | `perfil:qualquer` |
| GET | `/api/painel` | `perfil:qualquer` |
| GET | `/api/painel/regras` | `perfil:qualquer` |

O Auditor entra aqui: consultar decisão é o trabalho dele, e o analista precisa
das regras vigentes para entender o próprio score. Fechar a leitura junto com a
escrita teria sido simples e errado.

### Operação de fraude

| Verbo | Rota | Política |
|---|---|---|
| POST | `/api/casos` | `perfil:operacao` |
| POST | `/api/casos/{id}/alertas` | `perfil:operacao` |
| POST | `/api/casos/{id}/assumir` | `perfil:operacao` |
| POST | `/api/casos/{id}/transferir` | `perfil:operacao` |
| POST | `/api/casos/{id}/notas` | `perfil:operacao` |
| POST | `/api/casos/{id}/resolucao` | `perfil:operacao` |

### Supervisão

| Verbo | Rota | Política |
|---|---|---|
| GET | `/api/regras/tipos` | `perfil:supervisor` |
| GET | `/api/regras/gestao` | `perfil:supervisor` |
| GET | `/api/regras/{id}` | `perfil:supervisor` |
| POST | `/api/regras` | `perfil:supervisor` |
| PUT | `/api/regras/{id}/rascunho` | `perfil:supervisor` |
| DELETE | `/api/regras/{id}/rascunho` | `perfil:supervisor` |
| POST | `/api/regras/{id}/publicacao` | `perfil:supervisor` |
| POST | `/api/regras/{id}/ativacao` | `perfil:supervisor` |
| POST | `/api/regras/perfil/limiares` | `perfil:supervisor` |
| GET | `/api/backtests` | `perfil:supervisor` |
| GET | `/api/backtests/{id}` | `perfil:supervisor` |
| POST | `/api/backtests` | `perfil:supervisor` |
| POST | `/api/backtests/{id}/cancelamento` | `perfil:supervisor` |

### Auditoria

| Verbo | Rota | Política |
|---|---|---|
| GET | `/api/auditoria` | `perfil:auditoria` |
| GET | `/api/auditoria/operacoes` | `perfil:auditoria` |

---

## Invariantes verificadas

| Invariante | Teste |
|---|---|
| Toda rota exposta tem entrada na matriz | `Toda_rota_exposta_tem_uma_decisao_de_autorizacao_declarada` |
| A matriz não declara rota que não existe | `A_matriz_nao_declara_rota_que_nao_existe_mais` |
| Cada rota carrega exatamente a política declarada | `Cada_rota_carrega_exatamente_a_politica_declarada` |
| As rotas anônimas são só as cinco de sessão e saúde | `Nenhuma_rota_de_api_e_anonima_alem_das_de_sessao` |
| Nenhuma rota de escrita é alcançável por `perfil:qualquer` | `Toda_rota_de_escrita_humana_exige_perfil_mais_estreito_que_leitura` |

A última é a mais sutil: `perfil:qualquer` inclui o Auditor. Uma rota de
escrita com essa política daria a ele o poder de alterar o que deveria apenas
conferir — e ninguém perceberia lendo a rota isoladamente.

---

## Além da política: o tenant

A política responde *"este perfil pode?"*. Ela não responde *"este recurso é
dele?"* — isso é o filtro global de tenant, e a resposta para o recurso de
outra organização é `404`, e não `403`: um `403` já confirmaria que aquele
identificador existe em algum lugar (`CLAUDE.md` seção 52).

A varredura completa está em
`tests/CentralAntifraude.IntegrationTests/IsolamentoCompletoTests.cs`: um
recurso de cada família é montado no tenant B pelo caminho real do produto, e
cada identificador passa pela API do tenant A.
