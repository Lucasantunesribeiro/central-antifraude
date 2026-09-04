# Execução local

Guia para colocar a Central Antifraude rodando do zero.

## 1. Pré-requisitos

| Ferramenta | Versão | Para quê |
|---|---|---|
| .NET SDK | 10.0.400 ou superior | backend |
| Node.js | 22 ou superior | frontend |
| Docker | com o daemon no ar | PostgreSQL local e testes de integração |
| Git | qualquer recente | — |

A versão do SDK está fixada em `global.json` com `rollForward: latestFeature`.

## 2. Banco de dados

```bash
cp .env.example .env
# edite POSTGRES_PASSWORD no .env
docker compose up -d
```

O container sobe na porta definida por `POSTGRES_PORT` (padrão **5434**).

> **Por que 5434 e não 5432**
> A porta 5432 é a padrão do PostgreSQL e costuma já estar ocupada — por uma
> instalação nativa na máquina ou por outro projeto. Se as duas brigarem, o
> sintoma é confuso: `Host=localhost` resolve IPv4 primeiro e você acaba
> conectando no servidor errado, recebendo `password authentication failed`
> para um usuário que existe no seu container. Para descobrir quem está em
> cada porta:
>
> ```powershell
> Get-NetTCPConnection -State Listen |
>   Where-Object { $_.LocalPort -ge 5430 -and $_.LocalPort -le 5440 }
> ```

## 3. Migrations

```bash
export ConnectionStrings__Postgres="Host=localhost;Port=5434;Database=central_antifraude;Username=central_antifraude;Password=SUA_SENHA"
dotnet dotnet-ef database update --project src/CentralAntifraude.Infrastructure
```

`dotnet-ef` está no manifesto de ferramentas do repositório
(`dotnet-tools.json`). Se ainda não instalou:

```bash
dotnet tool restore
```

Para criar uma migration nova:

```bash
dotnet dotnet-ef migrations add <Nome> \
  --project src/CentralAntifraude.Infrastructure \
  --output-dir Persistencia/Migrations
```

## 4. Configuração da API

A string de conexão **não** está em `appsettings.json` — nenhum arquivo
versionado carrega credencial, e há um teste de arquitetura que garante isso.

Escolha uma das duas formas:

**Variável de ambiente** (o `__` é o separador de seção do .NET):

```bash
export ConnectionStrings__Postgres="Host=localhost;Port=5434;..."
```

**User secrets** (fica fora da pasta do projeto, no perfil do usuário):

```bash
dotnet user-secrets --project src/CentralAntifraude.Api \
  set "ConnectionStrings:Postgres" "Host=localhost;Port=5434;..."
```

Sem a string configurada, a API **não sobe** e diz o motivo. É deliberado: um
processo no ar que responde 200 no *liveness* e falha em toda operação é pior
do que um processo que não subiu.

## 4.1 Autenticação (Fase 1)

Duas configurações a mais, pelo mesmo caminho da string de conexão:

```bash
# Chave HMAC-SHA256 dos access tokens (base64, minimo 32 bytes).
export Autenticacao__ChaveDeAssinatura="$(node -e "console.log(require('crypto').randomBytes(48).toString('base64'))")"

# Senha dos usuarios de desenvolvimento. Sem ela o seed NAO roda.
export Seed__SenhaPadrao="escolha-uma-senha-longa"
```

Sem a chave, a API **não sobe** — é deliberado: uma configuração de
autenticação errada precisa impedir a inicialização, e não virar "token
inválido" em produção sem explicação.

```bash
# Chave HMAC do fingerprint de IP. O endereco nunca e persistido - so o HMAC.
export Ingestao__ChaveDeFingerprint="$(node -e "console.log(require('crypto').randomBytes(48).toString('base64'))")"
```

Sem ela a API também não sobe: aceitar transações e gravar um fingerprint
derivado de chave vazia produziria um valor reversível — ou seja, o IP.

O seed cria, apenas em Development e apenas se `Seed:SenhaPadrao` existir, uma
organização `demo` com um usuário de cada perfil:

| E-mail | Perfil |
|---|---|
| `admin@demo.local` | Administrador |
| `supervisor@demo.local` | SupervisorDeFraude |
| `analista@demo.local` | AnalistaDeFraude |
| `auditor@demo.local` | Auditor |

Todos com a senha de `Seed:SenhaPadrao`. O seed é idempotente: rodar de novo
não duplica nem sobrescreve senha.

O seed também provisiona o **catálogo de risco** da organização: o perfil padrão
(limiares 40 e 70) e as quatro regras, cada uma na versão 1. Isso não é
opcional — uma organização sem perfil ativo não consegue avaliar transação
nenhuma, e descobrir isso na primeira ingestão seria tarde.

### Ajuste opcional — janela de histórico

O motor carrega o histórico do cliente com dois limites, para que o custo de
avaliar seja previsível:

```bash
# Padrões. Só mexa se souber por quê.
export Avaliacao__DiasDeHistorico=90
export Avaliacao__MaximoDeTransacoesNoHistorico=200
```

`DiasDeHistorico` precisa cobrir ao menos 24 h — a maior janela que uma regra de
velocidade pode declarar. Um valor menor faria a regra perguntar por um passado
que o contexto não carregou e ficar **calada**: sem erro, sem aviso e com score
menor do que o perfil pede. A API recusa subir nesse caso.

### Ajuste opcional — concorrência

A ingestão roda em uma transação `SERIALIZABLE` e refaz a operação inteira
quando o banco recusa a ordem das transações simultâneas.

```bash
# Padrões. Medidos, não chutados — ver docs/adr/0009.
export Concorrencia__MaximoDeTentativas=8
export Concorrencia__EsperaBaseEmMs=10
export Concorrencia__EsperaMaximaEmMs=200
export Concorrencia__CustoPorLinha=1.0
```

`CustoPorLinha` vira `SET LOCAL cpu_tuple_cost` **dentro da transação crítica**,
e não muda o planejamento de nenhuma outra consulta. Ele existe porque a
documentação do PostgreSQL avisa que varredura sequencial obriga um bloqueio de
predicado da tabela inteira — e, com a tabela pequena, isso faz requisições de
clientes sem relação nenhuma conflitarem entre si.

Esgotadas as tentativas, a API responde **503 com `Retry-After`**. Não é erro:
é um convite a repetir com a mesma chave de idempotência.

### Ajuste opcional — fila e processos de fundo

Em `Development` o despachante da Outbox e o worker sobem junto com a API. Em
outros ambientes eles vêm **desligados**, e é deliberado: nos testes, um laço
rodando sozinho tornaria "quantas mensagens sobraram na fila" uma pergunta sem
resposta estável.

```bash
# Liga os dois laços de fundo dentro do processo da API.
export SegundoPlano__Habilitado=true
export SegundoPlano__IntervaloOciosoEmMs=2000
export SegundoPlano__IntervaloAtivoEmMs=100

# Parâmetros da fila. Os limites são os do próprio SQS.
export Fila__VisibilidadeEmSegundos=30
export Fila__MaximoDeRecebimentos=5
export Fila__TamanhoDoLote=10
```

`VisibilidadeEmSegundos` precisa ser confortavelmente maior que o
processamento mais lento: se expirar antes do fim, outro consumidor recebe a
mesma mensagem e o trabalho é jogado fora. `MaximoDeRecebimentos` é o
`maxReceiveCount` da política de redrive — sem ele, uma mensagem defeituosa
circula para sempre.

Para olhar a fila e a fila de mortas durante o desenvolvimento:

```sql
SELECT fila, count(*), min(disponivel_em) FROM fila_de_mensagens GROUP BY fila;
SELECT fila, recebimentos, motivo, movida_em FROM mensagens_mortas ORDER BY movida_em DESC;
SELECT count(*) FROM eventos_de_saida WHERE publicado_em IS NULL;
```

Não há endpoint para nenhuma das três: a DLQ guarda corpos de mensagens de
qualquer tenant, e a inspeção é por acesso administrativo ao banco.

## 5. Rodar

```bash
# backend — http://localhost:5175
dotnet run --project src/CentralAntifraude.Api

# frontend — http://localhost:5174
cd frontend && npm install && npm run dev
```

O `vite dev` faz proxy de `/health` e `/api` para `http://localhost:5175`, então
o navegador enxerga tudo na mesma origem. Isso evita configurar CORS agora: a
política de CORS pertence à Fase 11, junto do modelo de deploy real.

Confira:

```bash
curl http://localhost:5175/health/live
curl http://localhost:5175/health/ready

# Login (o -c grava o cookie de sessao num arquivo)
curl -i -X POST http://localhost:5175/api/auth/login   -H "Content-Type: application/json" -c cookies.txt   -d '{"email":"admin@demo.local","senha":"SUA_SENHA_DO_SEED"}'
```

As rotas de aplicação ficam sob `/api`; `/health/*` fica na raiz, porque é
consumido por orquestrador e não pela interface.

### Enviar uma transação

A ingestão usa credencial de integração, com esquema **próprio** — não é
`Bearer`, porque integração não é usuário:

```bash
# 1. Crie a integracao (como Administrador) e guarde a chave: ela aparece uma vez so.
curl -s -X POST http://localhost:5175/api/integracoes   -H "Authorization: Bearer $SEU_ACCESS_TOKEN"   -H "Content-Type: application/json"   -d '{"nome":"Checkout web"}'

# 2. Envie a transacao. Idempotency-Key e obrigatorio.
curl -i -X POST http://localhost:5175/api/ingestao/transacoes   -H "Authorization: ApiKey caf_..."   -H "Content-Type: application/json"   -H "Idempotency-Key: pedido-1001-tentativa-1"   -d '{
        "identificadorExterno": "pedido-1001",
        "valor": 249.90,
        "moeda": "BRL",
        "ocorridaEm": "2026-09-03T20:00:00Z",
        "clienteExternoId": "cli-777",
        "referenciaDoInstrumento": "pi_demo_123",
        "fingerprintDoDispositivo": "disp-abc",
        "enderecoIp": "203.0.113.10",
        "paisDeOrigem": "BR"
      }'
```

Repita o mesmo comando: a resposta vira **200** com `"situacao":"ja_registrada"`,
e continua havendo uma única transação. Mude o valor mantendo a chave: **409**.

> **Origem confiável.** Os endpoints de sessão recusam requisição cujo
> cabeçalho `Origin` não esteja na lista de origens permitidas — é a defesa
> contra CSRF. Em desenvolvimento, `http://localhost:5174` já vem declarado em
> `appsettings.Development.json`.
>
> Se o login funcionar no `curl` e responder **403 no navegador**, é isto: o
> `curl` não envia `Origin`, o navegador envia. Ajuste
> `Autenticacao:OrigensPermitidas`.

## 6. Testes

```bash
dotnet test tests/CentralAntifraude.UnitTests/CentralAntifraude.UnitTests.csproj
dotnet test tests/CentralAntifraude.ArchitectureTests/CentralAntifraude.ArchitectureTests.csproj
dotnet test tests/CentralAntifraude.IntegrationTests/CentralAntifraude.IntegrationTests.csproj

cd frontend && npm run test
```

Os testes de integração sobem o próprio PostgreSQL via Testcontainers — o
container do `docker-compose` **não** é usado por eles, e nada é gravado no seu
banco local.

## 7. Varredura de segredos

O CI roda o [gitleaks](https://github.com/gitleaks/gitleaks). Para rodar igual,
localmente:

```bash
gitleaks git --config .gitleaks.toml --redact --no-banner .
```

Use `git` e não `dir`: o modo `git` varre o histórico de commits, que é o que
de fato pode vazar. O modo `dir` também lê arquivos ignorados, como o seu
`.env`, e reporta um "vazamento" que nunca sairia da sua máquina.

---

## Armadilhas conhecidas desta máquina (Windows)

Registradas porque custaram tempo para diagnosticar.

### O SDK .NET 10 não está no PATH

O `dotnet` do PATH resolve `C:\Program Files\dotnet`, que tem apenas o SDK 9.
O SDK 10 está em `C:\Users\<usuario>\.dotnet` e só é alcançado por caminho
completo:

```bash
"/c/Users/<usuario>/.dotnet/dotnet.exe" build
```

### `dotnet test` pode passar sem executar nada

O runtime .NET 10 também está apenas em `C:\Users\<usuario>\.dotnet`. Um
executável de teste procura o runtime em `C:\Program Files\dotnet`, não acha, e
o `dotnet test` pode terminar com **código 0 sem ter rodado teste nenhum** —
verde que não prova nada.

Solução: apontar `DOTNET_ROOT` para a instalação certa.

```bash
export DOTNET_ROOT="C:\\Users\\<usuario>\\.dotnet"
```

Sempre confira a linha `Aprovado: N` na saída. Se `N` for zero ou não houver
resumo, nada rodou.

### `DOCKER_HOST` quebra o Testcontainers

Se `DOCKER_HOST` estiver na forma de quatro barras, o Testcontainers falha com
`The endpoint is not a npipe URI`:

```
npipe:////./pipe/docker_engine   ← quebra
npipe://./pipe/docker_engine     ← funciona
```

O projeto **corrige isso sozinho** dentro do processo de teste
(`tests/CentralAntifraude.IntegrationTests/Infra/NormalizacaoDoDockerHost.cs`).
A variável de ambiente da máquina não é alterada, porque ela pertence ao
desenvolvedor e outros projetos podem depender dela.
