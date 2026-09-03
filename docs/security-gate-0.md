# Security Gate 0 — Fundação Técnica

Data da execução: **2026-09-03**
Escopo: o que existe ao final da Fase 0. Não há autenticação, multi-tenancy
nem ingestão de transações — esses ganham os Gates 1 e 2.

---

## Itens exigidos pelo `ROADMAP.md` (seção 0.9)

### 1. Secrets fora do repositório — ✅

Nenhum arquivo versionado contém credencial.

- `appsettings.json` e `appsettings.Development.json` **não têm** string de
  conexão. A API lê de variável de ambiente ou de `dotnet user-secrets`.
- `.env.example` traz só o formato, com valor obviamente falso.
- A senha do PostgreSQL de teste é gerada por execução pelo Testcontainers e
  vive apenas enquanto o container existe.

**Verificação automática:**
`ContratosTransversaisTests.Nenhum_arquivo_de_configuracao_versionado_carrega_string_de_conexao`
varre todos os `appsettings*.json` de `src/` procurando padrão de credencial.

### 2. `.env` ignorado — ✅

`.gitignore` cobre `.env`, `.env.*` (com exceção explícita de `.env.example`),
`*.pem`, `*.key`, `*.pfx`, `*.p12`, `appsettings.Local.json` e `secrets.json`.

Confirmado: `git status` não lista `.env`, que existe na máquina local.

### 3. Configuração de desenvolvimento sem credencial real — ✅

O único valor parecido com senha em `src/` é o placeholder da fábrica de tempo
de design, escrito como `sem-uso-em-tempo-de-design`. Gerar migration não abre
conexão, então ele não autentica em lugar nenhum.

O `docker-compose.yml` lê a senha do `.env`, que não é versionado.

### 4. Stack traces não expostos no contrato de produção — ✅

`TratadorDeExcecoes` só deixa passar a mensagem de um `ErroDeAplicacao`.
Qualquer outra exceção vira 500 com texto genérico.

Mesmo em `Development` o stack trace fica **fora da resposta** — apenas o tipo
e a mensagem aparecem, e o rastreamento vai para o log.

**Verificação automática:** `TratadorDeExcecoesTests` lança uma exceção cuja
mensagem contém `"Npgsql: senha invalida para o usuario ... em 10.0.0.7"` e
afirma que nada disso aparece na resposta — nem a mensagem, nem o nome do tipo,
nem `"at CentralAntifraude"`, nem `"Npgsql"`.

Também coberto no nível HTTP: `ApiTests` roda a aplicação real em ambiente
`Production` e confere o 404 de rota inexistente.

### 5. JSON/DTOs não vinculados diretamente a entidades — ✅

Não há entidade persistida na Fase 0, então não há o que vincular. A proteção
estrutural, porém, já está no lugar:

`JsonUnmappedMemberHandling.Disallow` faz a API **recusar** payload com campo
desconhecido. Um corpo que traga `tenantId`, `score` ou `decisao` é rejeitado
por não estar no DTO, em vez de ser silenciosamente ignorado — que é o
mecanismo do *mass assignment*.

### 6. Dependências sem vulnerabilidade crítica conhecida — ✅

**.NET** — `dotnet list package --vulnerable --include-transitive` nos 7
projetos: nenhuma ocorrência.

**npm** — `npm audit`: `found 0 vulnerabilities`.

O CI repete os dois. A checagem do lado .NET lê o **JSON** e não o texto: a
mensagem legível muda com o idioma do runner, e um portão de segurança não pode
depender de locale.

### 7. Secret scanning verde — ✅

gitleaks 8.30.1, configurado em `.gitleaks.toml`, com as regras padrão mais
duas específicas do projeto (string de conexão PostgreSQL com senha; URL do
Neon com credencial).

Resultado na árvore atual: **`no leaks found`**.

**O scanner foi testado por mutação.** Um arquivo temporário com uma string de
conexão Neon realista e uma senha de produção foi criado; o gitleaks apontou
2 achados, identificando as duas regras customizadas. Removido o arquivo, a
varredura voltou a `no leaks found`.

Sem esse teste, "nenhum vazamento encontrado" seria indistinguível de um
scanner mal configurado que não encontra nada nunca.

---

## Decisões de segurança tomadas nesta fase

### Falha fechada na configuração

Sem string de conexão, a API **não sobe**. Um processo no ar respondendo 200 no
*liveness* e falhando em toda avaliação de risco seria pior do que um processo
que não iniciou.

### Cabeçalho de correlação validado

`X-Correlation-Id` volta na resposta e entra nos logs estruturados. Por isso o
valor vindo do cliente só é aproveitado se tiver de 8 a 64 caracteres em
`[A-Za-z0-9-_]`. Sem essa validação seria possível injetar cabeçalho e forjar
linha de log. Testado com `\r\n`, `<script>`, aspas e uma string de 5.000
caracteres.

### Endpoint de saúde pobre em informação

A resposta traz nome do componente e estado, nada mais. A mensagem de erro de
um health check de banco costuma conter host, porta, base e usuário — e é a
superfície mais exposta da aplicação.

Testado: com o banco inalcançável, a resposta é 503 e **não** contém o host, a
palavra `Username` nem `Npgsql`.

### 404 e não 403 para recurso fora do escopo

`RecursoNaoEncontrado` mapeia para 404. Quando a multi-tenancy chegar na Fase 1,
responder 403 para recurso de outro tenant já confirmaria que aquele
identificador existe.

### Ordenação por lista de permitidos

`ParametrosDeOrdenacao` nunca devolve o texto do cliente: devolve o nome
canônico da lista declarada pela consulta. Isso fecha o item "ordenação por
campo arbitrário" do Security Gate 10 antes de existir a primeira consulta.

---

## Fora de escopo nesta fase

Nomeado para não ser confundido com cobertura:

| Assunto | Fase |
|---|---|
| Autenticação humana, refresh rotation, CSRF | 1 |
| Isolamento de tenant | 1 |
| Credencial de integração, replay, mass assignment em payload real | 2 |
| Rate limiting | 2 e 11 |
| CORS e cookies | 11 |
| Threat model completo e matriz de autorização | 11 |
| Pentest gray-box | 15 |

**Não há CORS configurado.** É deliberado: em desenvolvimento o `vite dev` faz
proxy, e a política real depende do modelo de deploy (Vercel + Lambda), que só
é decidido na Fase 14.

---

## Resultado

**Security Gate 0: verde**, com as ressalvas de escopo acima explicitadas.

Nenhuma afirmação aqui é de que o sistema é seguro — apenas de que os itens
listados no `ROADMAP.md` para esta fase foram verificados, e como.
