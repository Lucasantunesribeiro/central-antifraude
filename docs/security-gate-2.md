# Security Gate 2 — Integrações e Ingestão

Data da execução: **2026-09-04**
Escopo: fronteira máquina-a-máquina, contrato de ingestão e idempotência.

Testes em `tests/CentralAntifraude.IntegrationTests/SecurityGate2Tests.cs` e
`IdempotenciaTests.cs`.

---

## Itens exigidos pelo `ROADMAP.md` (seção 2.10)

| # | Item | Resultado | Teste |
|---|---|---|---|
| 1 | API key inválida | ✅ 401 | `Credencial_invalida_ou_ausente_recebe_401` |
| 2 | Integração revogada | ✅ 401 imediato | `Credencial_revogada_para_de_funcionar_na_hora` |
| 3 | Tenant injection | ✅ 400 por campo desconhecido | `Tenant_injection_pelo_payload_e_recusado` |
| 4 | Mass assignment | ✅ 400 (JSON estrito) | idem |
| 5 | Payload desconhecido | ✅ 400 | idem |
| 6 | Payload grande | ✅ recusado | `Payload_grande_demais_e_recusado` |
| 7 | Moeda inválida | ✅ 400 | `Moeda_invalida_e_recusada` |
| 8 | Valor inválido | ✅ 400 | `Valor_nao_positivo_e_recusado` |
| 9 | Timestamp inválido | ✅ 400 | `Data_muito_no_futuro_e_recusada` |
| 10 | Replay | ✅ 4 cenários | `IdempotenciaTests` |
| 11 | Idempotency key collision | ✅ 409 sem sobrescrita | `Mesma_chave_com_conteudo_diferente_e_conflito` |
| 12 | Brute force / rate limit | ✅ por credencial | ver abaixo |
| 13 | Secret em logs | ✅ ausente | `A_chave_da_api_nao_aparece_no_log` |

---

## Os quatro cenários de idempotência (ROADMAP 2.11)

| Cenário | Esperado | Resultado |
|---|---|---|
| **Sequencial** — mesmo pedido 10×, mesma chave | 1 transação | ✅ 1 linha; todas as respostas apontam para o mesmo id |
| **Concorrente** — 20 requisições simultâneas | 1 transação | ✅ exatamente **1× 201 e 19× 200**, 1 linha |
| **Payload conflitante** — mesma chave, conteúdo diferente | conflito | ✅ 409 `idempotencia_conflitante`, e o valor original **não muda** |
| **External ID conflitante** — chave nova, mesmo identificador, conteúdo diferente | conflito | ✅ 409 `transacao_externa_conflitante` |

Dois cenários extras, porque expunham risco real:

- **10 requisições simultâneas com chaves diferentes e o mesmo
  `identificadorExterno`** → 1 transação. É a segunda barreira sob concorrência.
- **Diferença apenas de formatação** (`249.90` vs `249.9000`, fusos diferentes)
  → **não** é conflito. Tratar como conflito quebraria um retry legítimo de um
  cliente que serializa decimal de outro jeito.

---

## Detalhamento dos itens que exigem mais que um status

### Separação entre humano e máquina

Verificada nas duas direções:

- **token humano na ingestão** → 401;
- **API key em rota humana** → 401.

Os esquemas de autenticação são distintos (`Bearer` e `ApiKey`), e as políticas
declaram qual esquema aceitam. Não existe caminho em que uma credencial de
máquina vire sessão humana.

### Toda falha de credencial é indistinguível

Chave malformada, inexistente, segredo errado, credencial revogada, integração
desativada e organização suspensa produzem **a mesma resposta**. O teste
compara o corpo inteiro, ignorando apenas `traceId` e `idDeCorrelacao` — que
são únicos por requisição **por desenho**, e é assim que o suporte separa um
chamado do outro.

Distinguir daria a quem testa chaves um mapa do que existe.

O motivo real da recusa vai para o **log**, em nível Debug, citando o
identificador *público* da credencial — nunca o segredo.

### Desativar a integração corta o acesso na hora

Sem isso, "desativar" seria um rótulo na tela: quem tem a chave continuaria
enviando transações. A verificação acontece na autenticação, a cada
requisição — e as credenciais também são revogadas em cascata.

### Minimização de dados

- **O endereço IP não é persistido.** A transação guarda o HMAC-SHA256, e o
  teste confere no banco que o endereço não aparece em nenhuma coluna.
- **O mesmo IP produz o mesmo fingerprint** entre transações — é o que a regra
  de rede da Fase 3 vai precisar, sem guardar quem.
- **Não há campo para PAN, CVV ou conta bancária.** O contrato tem nove campos
  e mais nenhum.
- **PAN em campo de texto é recusado** pelo algoritmo de Luhn, com mensagem
  orientando o uso de token com prefixo.

### Segredo nunca reaparece

- A chave bruta sai **uma vez**, na resposta da emissão.
- `GET /api/integracoes/{id}` traz o identificador público — que é como o
  administrador reconhece qual chave está revogando — e nunca o segredo nem o
  hash.
- A trilha de auditoria é percorrida inteira pelo teste em busca do segredo e
  do prefixo `caf_`: nenhum aparece.
- O log do host de teste é varrido em busca do segredo: ausente. O EF Core
  mascara parâmetros (`@p='?'`), então nem o hash aparece — só o nome da
  coluna.

### Rate limiting

A ingestão é particionada pelo **identificador público da credencial**, e não
pelo IP: várias instâncias do integrador saem de IPs diferentes e são o mesmo
cliente; um limite por IP puniria uma delas sem proteger nada. Quando a chave
não pode ser lida — malformada ou ausente — a partição cai no IP, que é
justamente o caso de quem está testando chaves.

Limite: 600 requisições por minuto por credencial.

---

## Defeito real encontrado e corrigido

### O separador da credencial colidia com o alfabeto do segredo

A chave tem o formato `caf_{identificador}_{segredo}`, e o segredo é
**base64url** — alfabeto que inclui `_`, o mesmo caractere usado como
separador. Um `Split('_')` sem limite quebrava a chave em quatro pedaços e a
recusava.

**Como cerca de metade das chaves sorteadas contém `_`, o defeito aparecia em
metade das execuções.** Uma credencial emitida podia simplesmente nunca
autenticar, sem que nada no log dissesse por quê — a resposta era apenas 401.

Corrigido com `Split('_', 3)` nos dois lugares onde a chave é decomposta (o
interpretador de credencial e a partição do rate limiting).

**O teste que teria pego isso** agora existe e não depende de sorte: gera 500
chaves e exige que **todas** sejam interpretáveis, mais um caso escrito à mão
com `_` no segredo.

Foi durante esse diagnóstico que o handler ganhou o log de Debug com o motivo
da recusa — sem ele, a investigação continuaria às cegas.

---

## Fora de escopo nesta fase

| Assunto | Fase |
|---|---|
| Score, sinais e decisão de risco | 3 |
| `SERIALIZABLE` e retry de serialização | 4 |
| Outbox, SQS e consumidores idempotentes | 5 |
| CORS real e cookie cross-site | 11 |
| Threat model completo | 11 |
| Pentest gray-box | 15 |

**Limitações nomeadas:**

- A idempotência atual **não** cobre a avaliação de risco, porque ela ainda
  não existe. A invariante "replay não recalcula com regras novas"
  (`CLAUDE.md` seção 4.6) é da Fase 4.
- O rate limiting é por instância do processo. Com várias instâncias em
  Lambda, o limite efetivo se multiplica. Tratamento distribuído é da Fase 11.
- O detector de PAN tem falso positivo conhecido e assumido (ver ADR 0007).

---

## Resultado

**Security Gate 2: verde**, com as limitações e o escopo acima explicitados.

Nenhuma afirmação aqui é de que o sistema é seguro — apenas de que os itens
listados no `ROADMAP.md` para esta fase foram verificados, e como.
