# ADR 0007 — Ingestão de transações e idempotência

- **Status:** aceito
- **Data:** 2026-09-04
- **Fase:** 2 — Integrações e Ingestão de Transações

## Contexto

O `CLAUDE.md` (seção 32) trata idempotência como **requisito de domínio**, não
como detalhe de implementação. A mesma transação pode chegar duas vezes,
simultaneamente, com retry após timeout, com chave nova, ou atrasada — e o
sistema não pode produzir duplicidade de transação, avaliação ou alerta.

A seção 33 é explícita sobre o que **não** basta:

```
SELECT
if (!exists)
    INSERT
```

---

## Decisão 1 — Idempotência em três camadas

| Camada | O que faz | O que resolve |
|---|---|---|
| 1. Consulta antes de inserir | Procura transação equivalente | O caso comum: retry segundos depois, sem gerar erro no banco |
| 2. Restrição única no banco | `UNIQUE (organizacao, integracao, chave)` e `UNIQUE (organizacao, integracao, identificadorExterno)` | Concorrência real: duas requisições simultâneas consultam ao mesmo tempo, ambas acham nada, e **uma perde o INSERT** |
| 3. Nova consulta após o conflito | Quem perdeu a corrida encontra a linha da vencedora e devolve o mesmo resultado | Transformar a violação de constraint na resposta certa, em vez de um 500 |

Só a camada 1 é a armadilha que o `CLAUDE.md` proíbe. Só a camada 2 devolveria
erro onde deveria devolver sucesso. As três juntas dão o comportamento
correto **e** a garantia de que ele vale sob concorrência.

**Verificado:** 20 requisições simultâneas com a mesma chave produzem
exatamente **um 201, dezenove 200 e uma linha no banco**
(`IdempotenciaTests.Vinte_requisicoes_simultaneas_geram_uma_unica_transacao`).

### A tradução do erro do banco

O PostgreSQL sinaliza violação de unicidade com o SQLSTATE `23505`. A camada
de aplicação **não pode conhecer Npgsql** — é regra de arquitetura verificada
por teste. A tradução acontece em `UnidadeDeTrabalho.SalvarAsync`, que lança
`ConflitoDeUnicidadeNoBanco`, uma exceção da Application.

Ela também **desanexa** as entidades que ficaram em estado `Added`: sem isso, a
consulta seguinte — a que procura quem venceu a corrida — devolveria a
instância fantasma que nunca foi gravada.

---

## Decisão 2 — Fingerprint canônico do conteúdo

A idempotência precisa distinguir três situações:

- **mesma chave, mesmo pedido** → devolver o resultado original;
- **mesma chave, pedido diferente** → conflito (409);
- **mesmo identificador externo, pedido diferente** → conflito (409).

Comparar o corpo bruto não serve: dois JSON com os mesmos dados em ordem
diferente, ou com um espaço a mais, são o mesmo pedido — e seriam vistos como
conflito, quebrando um retry legítimo.

O hash é calculado sobre o conteúdo **já desserializado e normalizado**, em
ordem fixa de campos, com cada valor escrito exatamente como será persistido:

- datas em UTC — `2026-09-03T20:00:00Z` e `2026-09-03T17:00:00-03:00` são o
  mesmo instante e o mesmo hash;
- decimal com escala fixa — `249.9`, `249.90` e `249.9000` são o mesmo dinheiro
  e viram a mesma linha em `numeric(18,4)`;
- textos aparados; moeda e país em maiúsculas;
- ausente e vazio são a mesma coisa.

Cada campo é prefixado pelo próprio nome e separado por **ASCII 31** (separador
de unidade), que não aparece em nenhum valor válido. Sem isso, mover um
caractere de um campo para o vizinho produziria o mesmo hash — a ambiguidade
clássica de concatenação.

---

## Decisão 3 — Credencial de integração

Formato: `caf_{identificadorPublico}_{segredo}`

| Parte | Para quê |
|---|---|
| `caf_` | Prefixo fixo, para que varredores de segredo reconheçam a chave se ela vazar |
| identificador público (16 hex) | Localizar a linha por índice, em vez de comparar o hash contra todas |
| segredo (256 bits) | A prova de posse. Só o SHA-256 fica no banco |

**Por que SHA-256 e não PBKDF2** — mesma razão do refresh token (ADR 0006):
alongamento de chave compensa baixa entropia de senha humana, e 256 bits
sorteados não têm dicionário nem palpite. PBKDF2 no caminho crítico de
ingestão custaria centenas de milissegundos sem aumentar a segurança.

**Esquema HTTP próprio.** O cabeçalho é `Authorization: ApiKey ...`, não
`Bearer`. O `CLAUDE.md` (seção 50) exige que integrações não finjam ser
usuários humanos, e o esquema separado torna isso visível no código, no log e
na configuração de autorização — **não existe caminho em que uma API key vire
sessão humana, nem o contrário**. Ambos verificados por teste.

### Credencial é entidade separada, e não campo

Rotação sem interrupção exige que **duas credenciais valham ao mesmo tempo**: a
nova é distribuída, o integrador troca a configuração dele quando puder, e só
então a antiga é revogada. Com um campo único, toda rotação derrubaria a
ingestão do cliente no instante em que fosse feita.

Teto de duas credenciais ativas: bastam para a rotação, e um número maior só
aumentaria a superfície de chaves esquecidas e válidas.

---

## Decisão 4 — Contrato fechado, sem metadados livres

O DTO tem nove campos e mais nenhum. **Não há saco de metadados.**

Um dicionário aberto viraria, com o tempo, o lugar onde alguém coloca CPF,
nome e número de cartão "só por enquanto". Quando um campo novo for
necessário, ele entra no contrato, com nome, tipo e validação.

Note o que **não existe**: número de cartão, CVV, titular, conta bancária. Não
é uma regra que alguém precisa lembrar de seguir — **não há onde escrever
esses dados** (`CLAUDE.md` seções 56 e 58).

### Detector de PAN — defesa em profundidade

O contrato não tem campo de cartão, mas nada impediria alguém de colar o
número num campo de identificador. `DetectorDePan` aplica o algoritmo de Luhn
sobre sequências de 13 a 19 dígitos e recusa com mensagem orientadora.

**Falso positivo assumido, e por quê.** Um identificador puramente numérico de
13 a 19 dígitos tem cerca de 1 chance em 10 de passar no Luhn por acaso e ser
recusado. O custo é aceito de propósito:

- recusar um identificador numérico legítimo gera um erro claro, com a saída
  na própria mensagem — usar um token com prefixo, que é a prática esperada de
  qualquer forma;
- aceitar um PAN gera um dado que não deveria existir no banco, e para o qual
  não há desfazer.

---

## Decisão 5 — IP nunca é persistido

A transação guarda o **HMAC-SHA256** do endereço, nunca o endereço.

**Por que HMAC e não hash simples.** O espaço IPv4 tem 4 bilhões de valores —
uma tabela com o SHA-256 de todos eles cabe num disco comum. Sem chave
secreta, o "fingerprint" seria reversível por força bruta em minutos, e
guardar o hash equivaleria a guardar o IP.

Com HMAC, quem obtiver o banco sem a chave não volta ao endereço. O que
continua possível — e é o que a regra de risco precisa — é responder "estas
duas transações vieram do mesmo lugar?".

A chave vem de configuração e é obrigatória: sem ela a aplicação **não sobe**.

---

## Decisão 6 — Limites de sanidade, não regras de risco

| Limite | Valor | Por quê |
|---|---|---|
| Futuro máximo | 5 min | Diferença de relógio entre origem e servidor. Aceitar horas no futuro envenenaria as janelas temporais das regras de velocidade |
| Passado máximo | 30 dias | Evento atrasado é esperado e deve entrar (seção 16). O teto existe só para barrar data claramente errada — um ano de 1970 vindo de campo não preenchido |
| Valor máximo | `99.999.999.999.999` | Limite **físico** de `numeric(18,4)`, não regra de negócio |
| Corpo da requisição | 8 KB | O contrato inteiro cabe em centenas de bytes |

Nenhum destes é regra antifraude. Regra de risco começa na Fase 3, e lá os
limites pertencem ao perfil de risco do tenant, não à borda.

---

## Consequências

- Toda ingestão exige `Idempotency-Key`. É uma exigência a mais para o
  integrador, e é o que torna o retry seguro — a alternativa seria adivinhar
  quando duas requisições são a mesma.
- Uma transação registrada **não muda**. Conteúdo diferente com o mesmo
  identificador é conflito, nunca sobrescrita.
- `RecebidaEm` é cravado pelo relógio do servidor. Deixar a integração
  informá-lo permitiria mascarar um atraso — e é justamente a diferença entre
  os dois tempos que denuncia evento atrasado.
- A Fase 4 vai colocar a avaliação de risco dentro desta mesma operação. O
  ponto de extensão já está no lugar: a resposta hoje é um recibo, e passa a
  carregar a decisão sem mudar a semântica de idempotência.
