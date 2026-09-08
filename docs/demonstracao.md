# A demonstração — o que mostrar, e em que ordem

Este documento existe para uma situação específica: alguém tem oito minutos e
quer entender o produto. Sem ele, a tendência é abrir a tela mais bonita
primeiro, que raramente é a que explica alguma coisa.

---

## 1. As seis histórias que estão no banco

O seed não gera volume aleatório. Cada transação foi escrita para produzir um
sinal, e **as decisões não estão escritas à mão** — o seed monta a transação,
monta o contexto histórico e pergunta ao motor de risco de verdade, com a
versão de perfil publicada de verdade.

A consequência importa: se alguém mudar o peso de uma regra, os números desta
demonstração mudam junto. Um seed que gravasse `score = 75` viraria mentira na
primeira alteração do catálogo.

| | História | Cliente | O que acontece | Decisão |
|---|---|---|---|---|
| **A** | Movimento normal | `cli-ana-recorrente` | 6 compras, mesmo aparelho, mesmo país | Permitir |
| **B** | Compra atípica | `cli-bruno-viajante` | R$ 920 contra histórico de R$ 100, aparelho novo, outro país | **Bloquear** |
| **C** | Velocidade | `cli-carla-apressada` | 4 tentativas em 9 minutos | Revisar |
| **D** | Sinal isolado | `cli-diego-fiel` | compra de outro país, valor normal, aparelho conhecido | **Permitir** |
| **E** | Falso positivo | `cli-elis-madrugada` | rajada + aparelho recém-trocado | Revisar → **Legítima** |
| **F** | Mesmo padrão | `cli-fabio-parceiro` | segunda ocorrência do padrão de B | Bloquear |

**A história D é a menos óbvia e a mais útil de explicar.** Divergência
geográfica sozinha vale +25, e o limiar de revisão é 40: a transação passa com
o sinal registrado. É a demonstração de que o score é aditivo — uma evidência
sozinha levanta a sobrancelha e não segura ninguém. Sem um caso assim, quem vê
o produto conclui que todo sinal vira alerta.

E daí sai a pergunta que separa quem entendeu de quem não entendeu: *e quando
um sinal sozinho deveria bastar?* A resposta é o perfil de risco — muda-se o
limiar, ou o peso da regra, e publica-se uma nova versão. É a deixa para a tela
de Regras.

### As duas investigações

- **Caso resolvido — o falso positivo.** Aberto a partir do alerta de `Revisar`
  da história E, assumido, com nota de apuração e resolvido como **Legítima**.
- **Caso em aberto — dois alertas.** As histórias B e F, agrupadas: é o que
  distingue um caso de um alerta solto.

---

## 2. O roteiro, em ordem

Oito minutos, uma tela de cada vez. Entre como **`analista@demo.local`**.

### Painel operacional — 1 min

Comece pelos números, não pelas telas. *Recebidas* e *avaliadas* são diferentes
de propósito: uma conta pela chegada e a outra pela decisão, e uma transação
atrasada chega hoje sobre um fato de ontem.

Aponte para **Permitir com a maioria absoluta**. Antifraude que barra metade do
movimento não é antifraude, é prejuízo.

### Alertas — 1 min

A fila de trabalho. Ordene por score. Repare que a decisão tem **cor, forma e
texto** ao mesmo tempo — círculo, losango e triângulo —, então ela continua
legível para quem não distingue as cores e numa captura em preto e branco.

### A transação bloqueada — 2 min

Abra `demo-b-compra-suspeita`. **É a tela que explica o produto.**

O score de 75 não é um número que saiu de um modelo: são três regras nomeadas,
cada uma com os pontos que contribuiu e a evidência que a acionou — valor
contra a média histórica, país anterior contra país atual, impressão do
aparelho. É isso que significa "explicável".

Diga em voz alta: *nada disso é IA*. O motor é determinístico — mesma entrada,
mesma versão de regras, mesmo resultado.

### O caso resolvido — 2 min

Abra o caso **Compras seguidas de um aparelho recém-trocado**.

Leia a linha do tempo: aberto, assumido, nota, resolvido como Legítima. E então
mostre a parte que quase todo mundo perde: **a decisão automática continua sendo
Revisar**. O resultado humano registra que o motor errou; ele não reescreve o
que o motor decidiu.

É por isso que existe investigação humana no produto, e é a única forma honesta
de medir se uma regra está boa.

### Regras e backtest — 2 min

Entre como **`supervisor@demo.local`**.

Mostre que uma regra publicada é **imutável**: alterar cria uma versão nova, e
a avaliação de ontem continua explicável pela versão que valia ontem. Depois
rode um backtest — o mesmo motor, sobre dados históricos, sem tocar em produção
e sem criar alerta nenhum.

A pergunta que ele responde é a que todo time de fraude faz: *se eu tivesse
publicado esta regra, o que teria mudado?*

### Auditoria — 30 s

Entre como **`auditor@demo.local`** ou **`admin@demo.local`**. A trilha é
somente-inserção: não existe rota que a altere. Feche mostrando que o Auditor
enxerga tudo e não pode agir sobre nada.

---

## 3. As contas

Todas na organização `demo`. A senha é a que estiver em `Seed:SenhaPadrao` — ela
nunca está no código nem neste documento.

| Conta | Perfil | Para mostrar |
|---|---|---|
| `analista@demo.local` | Analista de fraude | fila, alertas, casos |
| `supervisor@demo.local` | Supervisor | regras e backtests |
| `auditor@demo.local` | Auditor | leitura e trilha |
| `admin@demo.local` | Administrador | usuários, integrações, auditoria |

**Sobre uma conta pública de demonstração** (ROADMAP 13.4): o perfil adequado é
o **Auditor** — leitura, sem ação destrutiva. O Analista altera estado (assume
casos, resolve investigações), e uma demonstração pública com ele exigiria uma
estratégia de reset. Enquanto não houver deploy público, esta é a
recomendação registrada, e não uma configuração aplicada.

---

## 4. Reproduzir a demonstração do zero

O seed é **idempotente**: rodar de novo não duplica nada. O marcador é a própria
primeira transação da história A.

```bash
# 1. Banco limpo (destrói os dados locais — só faça em desenvolvimento).
docker rm -f central-antifraude-postgres
docker volume rm central-antifraude-dados

# 2. Suba de novo conforme docs/setup-local.md e rode a API em Development
#    com Seed:SenhaPadrao configurado. O seed cria organização, usuários,
#    catálogo de regras e as seis histórias.
```

Para conferir sem abrir a tela:

```bash
dotnet test tests/CentralAntifraude.IntegrationTests \
  --filter "FullyQualifiedName~SeedNarrativoTests"
```

Oito testes afirmam que as histórias continuam acontecendo — inclusive o falso
positivo, que é o único item da demonstração exigido nominalmente pelo
`CLAUDE.md` (seção 94).

---

## 5. O que a demonstração deliberadamente **não** mostra

Dizer isto em voz alta vale mais do que esconder:

- **Não há dinheiro.** O produto recomenda `Permitir`, `Revisar` ou `Bloquear`;
  ele não autoriza, não captura e não liquida.
- **Não há IA na decisão.** Nem no score, nem no sinal, nem na resolução.
- **Não há dado real.** Nenhuma PII, nenhum cartão: o instrumento de pagamento
  é sempre uma referência `pi_demo_*`, e o cliente é um apelido.
- **Os limiares são fictícios.** `40` e `70` são configuração de demonstração,
  e não padrão de mercado (`CLAUDE.md` seção 112).
