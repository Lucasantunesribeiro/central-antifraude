# ADR 0004 — Contratos transversais da API

- **Status:** aceito
- **Data:** 2026-09-03
- **Fase:** 0 — Fundação Técnica

Decisões que valem para **toda** a API e que o `ROADMAP.md` (seção 0.5) manda
congelar antes de existir qualquer endpoint de negócio. Cada uma tem um teste
que a segura.

---

## 1. Tempo — UTC, sempre, e por um relógio injetado

A Central Antifraude distingue três instantes (`OccurredAt`, `ReceivedAt`,
`EvaluatedAt`) e terá regras de velocidade dependentes de janela temporal.

- Instantes usam `DateTimeOffset` e são persistidos em UTC (`timestamptz`).
- Nenhum código de `src/` lê o relógio direto: `DateTime.UtcNow`,
  `DateTime.Now`, `DateTime.Today`, `DateTimeOffset.Now` e
  `DateTimeOffset.UtcNow` estão em `src/BannedSymbols.txt` e **quebram a
  compilação**.
- A única fonte é `IRelogio` (Domain), implementada por `RelogioSistema`
  (Infrastructure).

Sem isso, testar "5 tentativas em 5 minutos" exigiria `Thread.Sleep` — teste
lento e intermitente, o pior tipo.

**Testes:** `RelogioSistemaTests` (offset zero) e
`ContratosTransversaisTests.A_leitura_do_relogio_real_acontece_em_um_unico_lugar`,
que falha se aparecer uma terceira supressão de `RS0030` em `src/`.

---

## 2. Dinheiro — `decimal` com moeda explícita

`Dinheiro` (Domain) é `decimal` + código ISO 4217. `float`/`double` estão fora:
`0.1 + 0.2` em ponto flutuante binário não dá `0.3`, e valor de transação
gravado diferente do recebido é defeito grave num sistema antifraude.

Escala máxima 4, alinhada a `numeric(18,4)`. Um valor com 5 casas é **recusado**
na borda, não arredondado em silêncio pela coluna.

`Dinheiro` não decide se negativo é válido: isso é invariante do contrato que o
usa, não do tipo.

**Testes:** `DinheiroTests`.

---

## 3. Erros — Problem Details (RFC 9457)

Categorias em `TipoDeErro`, com status HTTP próprio:

| Categoria | HTTP |
|---|---|
| `Validacao` | 400 |
| `NaoAutenticado` | 401 |
| `NaoAutorizado` | 403 |
| `NaoEncontrado` | 404 |
| `Conflito` | 409 |
| `LimiteDeRequisicoes` | 429 |
| `Interno` | 500 |

Regras:

- Só a mensagem de um `ErroDeAplicacao` chega ao cliente. Qualquer outra
  exceção vira 500 com texto genérico — stack trace, tipo da exceção, nome de
  tabela e detalhe de driver **nunca** atravessam a borda.
- Recurso de outro tenant devolve **404**, não 403: responder 403 já
  confirmaria que aquele identificador existe.
- Todo erro carrega `idDeCorrelacao`, inclusive os que não passam por exceção
  (404 de rota, 405). Isso vem de `CustomizeProblemDetails`, num lugar só.
- Cliente que desiste da requisição vira 499, não 500 — não é falha do
  servidor e não deve poluir a métrica de erro.

**Testes:** `TratadorDeExcecoesTests` (inclusive um caso que verifica que
nenhuma categoria nova cai silenciosamente em 500) e
`ApiTests.Rota_inexistente_devolve_problem_details_e_nao_corpo_vazio`.

---

## 4. JSON — estrito

- `camelCase`, sem tolerância a diferença de maiúsculas.
- **Campo desconhecido é recusado** (`JsonUnmappedMemberHandling.Disallow`).
  Um integrador que escreve `amout` no lugar de `amount` precisa descobrir na
  primeira chamada, não depois de mil transações avaliadas sem o valor.
- Enums viajam como **texto**: `"Revisar"` é contrato estável; o inteiro `1`
  muda de significado se alguém reordenar o enum.
- Sem vírgula final, sem comentário, sem número em string.

Isso também fecha, por construção, a porta do *mass assignment*: um payload que
traga `tenantId` ou `score` é rejeitado por conter campo não mapeado no DTO.

---

## 5. Correlação

Cabeçalho `X-Correlation-Id`, presente na resposta de toda requisição.

O valor do cliente é **aproveitado** — é o que liga um incidente no sistema do
integrador ao log da Central Antifraude — mas só se tiver de 8 a 64 caracteres
em `[A-Za-z0-9-_]`. O valor volta num cabeçalho de resposta e entra nos logs
estruturados; aceitar texto livre permitiria forjar linha de log e injetar
cabeçalho.

O middleware é o **primeiro** do pipeline, antes do tratador de exceções. Na
ordem inversa, a linha de log da falha sairia sem `CorrelationId` — justamente
a linha que alguém procura durante um incidente.

**Testes:** `MiddlewareDeCorrelacaoTests` e os casos de correlação em `ApiTests`.

---

## 6. Paginação e ordenação

- Página começa em 1; tamanho padrão 25, máximo 100.
- Valor fora da faixa é **recusado**, não reduzido em silêncio. Numa tela de
  alertas de fraude, acreditar que viu tudo é pior do que receber um erro.
- O campo de ordenação **nunca** vem livre do cliente: cada consulta declara a
  sua lista de permitidos, e o valor devolvido é sempre o nome canônico dessa
  lista — nunca o texto digitado. Isso fecha, já na Fase 0, o item "ordenação
  por campo arbitrário" do Security Gate 10.

**Testes:** `ConsultaPaginadaTests`.

---

## 7. Cancellation tokens

Todo método assíncrono público de Application aceita `CancellationToken`.

Sem isso, uma consulta pesada continua ocupando conexão do banco depois que o
cliente HTTP já desistiu — e numa Lambda isso é tempo cobrado sem ninguém
esperando a resposta.

**Teste:**
`ContratosTransversaisTests.Metodo_assincrono_publico_da_aplicacao_aceita_cancellation_token`.
Hoje ele passa por vacuidade — Application ainda não tem método assíncrono.
Entra agora porque é barato, e passa a valer sozinho conforme as fases
seguintes acrescentam código.

---

## 8. Health checks separados

- `/health/live` — o processo está vivo. **Não toca em dependência externa.**
- `/health/ready` — as dependências respondem.

Juntá-los faria uma queda do PostgreSQL reiniciar processos saudáveis em
cascata, transformando indisponibilidade parcial em total.

A resposta traz nome do componente e estado, nada mais: a mensagem de erro de
um health check de banco costuma conter host, porta, base e usuário — e o
endpoint de saúde é a superfície mais exposta de uma aplicação.

**Testes:** `ApiTests`, incluindo o caso que aponta para um banco inalcançável
e confirma 503 sem vazamento de topologia. Sem esse caso, um health check que
sempre devolvesse `Healthy` também passaria.
