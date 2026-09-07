# Security Gate 10 — Console, painel e auditoria

Data da execução: **2026-09-07**
Escopo: a primeira fase em que a superfície nova é **consulta** — filtro livre,
ordenação escolhida pelo cliente, paginação e agregação.

Testes em `tests/CentralAntifraude.IntegrationTests/SecurityGate10Tests.cs` e
`ConsoleOperacionalTests.cs`,
`tests/CentralAntifraude.UnitTests/Aplicacao/FiltrosDoConsoleTests.cs`.

---

## A premissa desta fase

Nenhuma rota desta fase escreve. É exatamente por isso que elas são perigosas:
**uma consulta que vaza não deixa rastro no dado, só no que alguém viu.**

Daí os três riscos próprios do gate:

1. **texto do cliente virando consulta** — filtro, busca e campo de ordenação
   chegam como texto e precisam parar antes do SQL;
2. **agregação atravessando tenant** — um painel que somasse organizações
   devolveria números plausíveis, e nada denunciaria;
3. **a trilha na mão errada** — auditoria é um controle sobre quem opera, e
   quem é auditado não pode varrer o próprio rastro.

---

## Itens exigidos pelo `ROADMAP.md` (seção 10.8)

| # | Item | Resultado | Teste |
|---|---|---|---|
| 1 | Filtros com SQLi | ✅ 4 payloads, todos com zero resultado e tabela intacta | `Injecao_na_busca_e_tratada_como_texto` |
| 2 | Paginação abusiva | ✅ 4 formas no console + 1 na auditoria + 3 janelas do painel, todas `400` | `Paginacao_abusiva_e_recusada_no_console`, `Paginacao_abusiva_e_recusada_na_auditoria`, `Janela_abusiva_do_painel_e_recusada` |
| 3 | Busca cross-tenant | ✅ identificador conhecido do outro tenant não aparece | `A_busca_nao_atravessa_organizacao`, `Transacao_de_outro_tenant_responde_404_no_detalhe` |
| 4 | Auditoria vazando secrets | ✅ 8 palavras proibidas, sobre registros reais | `A_trilha_nao_devolve_segredo_nenhum` |
| 5 | CSV injection | ⚪ **não aplicável** — não há exportação (ROADMAP 10.7, ADR 0015 decisão 10) | — |
| 6 | Dashboard misturando tenants | ✅ conferido na resposta **e no banco** | `O_painel_nao_mistura_organizacoes`, `As_metricas_de_regra_nao_misturam_organizacoes` |
| 7 | Enum injection | ✅ 5 formas no console + 2 na auditoria, todas `400` | `Enum_forjado_no_filtro_e_recusado`, `Enum_forjado_no_filtro_de_auditoria_e_recusado` |
| 8 | Ordenação por campo arbitrário | ✅ 3 campos, todos `400` | `Ordenacao_por_campo_arbitrario_e_recusada` |

---

## 1. Texto do cliente virando consulta

O EF Core parametriza, então nenhum dos payloads vira comando. O que este gate
afirma é o **passo seguinte**: a consulta responde, com zero resultado, e a
tabela continua de pé depois — verificado com uma segunda busca que encontra o
dado.

```text
' OR 1=1 --
'; DROP TABLE transacoes; --
" UNION SELECT * FROM usuarios --
1' AND (SELECT 1 FROM pg_sleep(5))='1
```

**O curinga é o risco real, e não a injeção.** `%` e `_` são operadores do
`LIKE`: um `%` digitado por engano devolveria a tabela inteira e a tela
pareceria filtrada. Isso é pior do que um erro — uma lista que volta cheia não
levanta suspeita.

O escape acontece em `FiltroDeTransacoes.PadraoDeBusca`, com teste unitário
sobre os três metacaracteres (`%`, `_`, `\`) e teste de integração provando que
`%l` não encontra o que `literal` encontra.

**O campo de ordenação nunca vem livre.** Ele é comparado com uma lista fechada
e substituído pelo canônico — decisão da Fase 0, cobrada aqui com `senha`,
`organizacaoId` e `t.Id; DROP TABLE transacoes`.

**O número é recusado onde se espera um nome.** `Enum.TryParse` aceitaria
`decisao=2` como `Revisar` e `decisao=99` como um valor que não existe no enum.
Comparar com os nomes fecha os dois buracos — e o mesmo vale para
`operacao=63` na auditoria.

---

## 2. Paginação e janela

| Entrada | Resultado |
|---|---|
| `tamanho=5000`, `tamanho=0` | `400` |
| `pagina=0`, `pagina=-3` | `400` |
| `auditoria?tamanho=100000` | `400` |
| `painel?dias=0`, `dias=-1`, `dias=3650` | `400` |

Reduzir em silêncio para o teto faria o cliente acreditar que recebeu a lista
inteira. Numa tela de fraude, acreditar que se viu tudo é pior do que receber um
erro.

A janela do painel tem limite próprio porque ele **agrega**: dez anos varreriam
o histórico inteiro do tenant para responder uma pergunta que ninguém fez.

---

## 3 e 6. Isolamento entre tenants

Nenhuma consulta desta fase desliga o filtro global. Onde um
`IgnoreQueryFilters` seria tecnicamente possível — a junção com sinais, por
exemplo — ele não aparece, de propósito.

| Verificação | Resultado |
|---|---|
| Busca pelo identificador conhecido do outro tenant | zero resultado; o dono encontra |
| Detalhe de transação de outro tenant | `404`, e não `403` |
| Painel de A × painel de B | contagens independentes, conferidas **no banco** |
| Métricas de regra de B, com dados só em A | lista vazia |
| Trilha de A × trilha de B | conjuntos de identificadores disjuntos |

O painel é o ponto mais sensível: **um painel que somasse organizações
devolveria números plausíveis, e nada denunciaria.** Por isso a verificação não
para na resposta HTTP — a contagem é lida do banco com o contexto do tenant B.

---

## 4. A trilha não vaza

A trilha nunca recebeu senha, hash, token nem payload completo — a decisão é da
Fase 1. Este gate cobra a promessa **na saída**, sobre registros reais: o
preparo do teste cria integração e credencial, que são justamente as operações
com segredo por perto.

Oito palavras proibidas no corpo da resposta:

```text
senha · password · hash · token · authorization · bearer · caf_ · secret
```

O campo `detalhe` carrega um resumo curto e seguro — `"Perfil: Auditor ->
Analista"` — que é o "antes/depois" que o ROADMAP 10.6 pede, na forma que não
vaza.

---

## Quem lê a trilha

| Perfil | Resultado |
|---|---|
| Administrador | `200` |
| Auditor | `200` |
| Supervisor de fraude | `403` |
| Analista de fraude | `403` |

A trilha é um controle **sobre** o que Supervisor e Analista fazem — publicar
regra, resolver caso. Dar a quem é auditado o poder de varrer o próprio rastro
enfraquece o único registro que responde "quem fez o quê e quando".

**Nenhuma rota altera a trilha.** `POST`, `PUT`, `DELETE` e `PATCH` em
`/api/auditoria` respondem `404` ou `405`. A ausência da rota é a garantia — não
um `if`.

---

## Fora do `ROADMAP.md`, mas do mesmo gate

| Verificação | Resultado |
|---|---|
| Console, painel, métricas e auditoria sem token | `401` |
| Credencial de integração (`ApiKey`) numa rota humana | `401` |
| Faixa de score invertida ou fora de 0..100 | `400` |
| Período invertido no console e na auditoria | `400` |
| Busca com menos de 2 ou mais de 100 caracteres | `400` |

---

## O que este gate **não** cobre

Nomeado para não ser confundido com cobertura:

- **Sem exportação, e portanto sem CSV injection.** É decisão registrada
  (ADR 0015, decisão 10), e não pendência. Se a exportação entrar depois, o
  item 5 deste gate deixa de ser não aplicável.
- **Sem rate limit próprio** nas rotas de consulta. Elas são autenticadas e
  limitadas por paginação e janela; rate limiting distribuído é a Fase 11.
- **Sem teste de tempo de resposta sob volume.** As consultas usam os índices
  existentes mais o novo `(organizacao, ocorrida_em)`, mas o comportamento com
  milhões de linhas não foi medido — performance sob carga é a Fase 12.
- **Sem mascaramento na saída.** Não é necessário: o que a trilha guarda já é o
  que pode ser exibido, e a transação nunca teve PAN, CVV nem IP bruto.
