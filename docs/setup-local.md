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
```

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
