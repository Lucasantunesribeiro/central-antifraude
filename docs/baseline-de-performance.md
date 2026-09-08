# Baseline de performance — caminho crítico

Medições das Fases 4 e 12. **Isto não é um SLA.**

O `ROADMAP.md` seção 4.8 pede explicitamente que não se invente número de
banco. O que está aqui é uma referência do ambiente medido, para que uma
regressão de ordem de grandeza apareça no CI antes de alguém descobri-la em
produção.

---

## Ambiente medido

| Item | Valor |
|---|---|
| Data | 2026-09-04 |
| Máquina | Windows 10 Pro, desenvolvimento |
| Banco | PostgreSQL 17 (Alpine) em Docker, mesma máquina |
| API | ASP.NET Core hospedada em memória (`WebApplicationFactory`) |
| Build | Debug |
| Isolamento | `SERIALIZABLE` na operação crítica |

Tudo roda na mesma máquina, sem rede real entre API e banco. Números de
produção serão diferentes — em produção existe latência de rede até o Neon, e
não existe o custo do host de teste.

---

## Ingestão sequencial

40 amostras, após 2 requisições de aquecimento. Cada requisição faz o caminho
completo: autenticação da integração, validação, idempotência, contexto
histórico, motor de risco, gravação de transação + avaliação + sinais + evento,
commit.

| Percentil | Latência |
|---|---|
| p50 | **15,4 ms** |
| p95 | **28,7 ms** |
| p99 | **39,9 ms** |

Teste: `BaselineDePerformanceTests.Ingestao_sequencial_fica_dentro_da_ordem_de_grandeza_esperada`.

O teto verificado no CI é de **2 s no p99** — duas ordens de grandeza acima do
observado. É deliberado: um limite apertado em máquina de desenvolvimento vira
teste intermitente, e teste intermitente acaba desligado, o que é pior do que
não medir.

---

## Custo do histórico

O risco desta fase é a avaliação ficar mais lenta conforme o cliente acumula
passado. O contexto tem dois limites — 90 dias e 200 transações — justamente
para que isso não aconteça.

| Cenário | Latência |
|---|---|
| Cliente sem histórico | ~15 ms (p50 acima) |
| Cliente com 60 transações anteriores | **14,5 ms** |

Teste: `BaselineDePerformanceTests.Historico_longo_do_mesmo_cliente_nao_degrada_a_avaliacao`.

Sem diferença mensurável, porque a consulta usa
`ix_transacoes_organizacao_id_cliente_externo_id_ocorrida_em` e para no `LIMIT`.

---

## Número de consultas

Medida que **não depende da máquina**, e por isso a mais útil no CI: um N+1
introduzido por engano aparece como um salto mesmo em um notebook lento.

| Operação | Comandos SQL |
|---|---|
| Ingestão de transação nova | **8** |

Onde eles vão:

1. `SET LOCAL cpu_tuple_cost` (ver ADR 0009, Decisão 5);
2. busca por chave de idempotência;
3. busca por identificador externo;
4. versão vigente do perfil, com as versões de regra no mesmo carregamento;
5. contexto histórico do cliente;
6. gravação da transação;
7. gravação de avaliação, sinais e evento;
8. `COMMIT`.

O teto verificado é 25, com folga para variação de plano — mas **não** para uma
consulta por regra ou uma por sinal.

Teste: `BaselineDePerformanceTests.A_operacao_critica_usa_um_numero_previsivel_de_consultas`.

---

## Concorrência

Medido durante o ajuste do orçamento de retentativas, com a tabela `transacoes`
ainda pequena — o pior caso para o isolamento serializável (ADR 0009,
Decisão 5).

| Cenário | Resultado |
|---|---|
| 12 requisições simultâneas, 12 clientes distintos | 12 criadas, 28 retentativas no total |
| 6 requisições simultâneas, mesmo cliente | 6 criadas, contagens de janela {1..6} sem repetição |
| 20 requisições simultâneas idênticas | 1× 201, 19× 200, 1 transação, 1 avaliação, 1 evento |

Com o orçamento anterior de 4 tentativas, o primeiro cenário respondia **2×
503**. Com 8, nenhum.

---

## Fase 12 — os quatro cenários de carga

Medidos em 2026-09-07, no mesmo ambiente descrito acima.
Teste: `CargaTests`. Os números saem na saída do teste, e não são afirmados
como limite — os tetos verificados no CI ficam uma ordem de grandeza acima.

| Cenário | Forma | Vazão | p50 | p95 | p99 |
|---|---|---|---|---|---|
| **A** — throughput normal | 8 clientes × 8 transações | **56/s** | 19 ms | 463 ms | 850 ms |
| **B** — cliente quente | 20 simultâneas, mesmo cliente | 20/s | 833 ms | 987 ms | 1.015 ms |
| **C** — tempestade de idempotência | 25× a mesma requisição | **156/s** | 120 ms | 125 ms | 156 ms |
| **D** — acúmulo assíncrono | 60 eventos represados | 78/s | — | — | — |

Leitura de cada um:

- **A.** 63 aceitas e **1 em contenção** (503). Ver a seção seguinte: o número
  não vem do domínio.
- **B.** O caso que o isolamento forte torna caro de propósito — todas leem a
  mesma janela histórica. Nenhuma inconsistência, nenhum 500.
- **C.** Vinte e cinco repetições custam o que **uma** custa: a idempotência
  reconhece a duplicata antes de avaliar. Um cliente com retry agressivo não
  multiplica a carga do motor.
- **D.** Drenado em 6 ciclos, sem perda, sem duplicata e com a fila de mortas
  vazia ao final.

---

## Fase 12 — o número que contraria a intuição

No cenário A, **clientes independentes entraram em contenção de serialização**.
Eles não compartilham dado nenhum: a janela histórica de um não inclui as
transações do outro.

A causa não está no domínio, e sim no plano de execução. Sob `SERIALIZABLE`, o
PostgreSQL toma predicado sobre o que a consulta **lê** — e uma varredura
sequencial lê a relação inteira, então o predicado cobre a tabela toda e todos
passam a conflitar com todos. Com a tabela pequena, varrer é legitimamente mais
barato do que abrir índice, e o planejador escolhe varrer.

A Fase 4 já tinha encontrado isso e mitigado com `SET LOCAL cpu_tuple_cost`
(ADR 0009, Decisão 5). O que esta medição acrescenta é o limite da mitigação:
ela **reduz**, mas não elimina, enquanto o volume for baixo. O que elimina é
volume — e a próxima seção mostra o plano com dados.

Consequência prática: nenhuma. O contrato já devolve `503` com `Retry-After`, e
repetir com a mesma chave de idempotência é seguro. Registrado porque um número
inexplicado numa medição de carga é pior do que um número ruim explicado.

---

## Fase 12 — planos de execução com volume

4.000 transações e 4.000 eventos semeados, com `ANALYZE` antes de medir.
Teste: `DesempenhoDeConsultasTests`.

| Consulta | Plano |
|---|---|
| Janela histórica do cliente | `Index Scan Backward using ix_transacoes_organizacao_id_cliente_externo_id_ocorrida_em` (custo 0,29..157,41) |
| Primeira página do console | `Index Scan Backward using ix_transacoes_organizacao_id_ocorrida_em` (custo 0,29..5,57) |
| Outbox pendente | `Bitmap Index Scan on ix_eventos_de_saida_pendentes` (custo 0,00..8,54) |

Três coisas que estes planos provam:

1. **a ordem das colunas do índice é o que o faz servir** — organização,
   cliente e só então tempo. Invertida, ele responderia "todas as transações de
   setembro", que ninguém pergunta;
2. **o `LIMIT` é alcançado pelo índice**, e não por ordenação posterior: o custo
   da primeira página do console é 5,57 sobre 4.000 linhas;
3. **o índice parcial da Outbox funciona**. Ela é uma tabela que só cresce —
   milhões publicadas, dezenas pendentes —, e um índice sobre tudo indexaria
   justamente as linhas que ninguém procura.

**Nenhum índice novo foi criado nesta fase.** O `ROADMAP.md` 12.8 exige
evidência antes de acrescentar índice, e a evidência disse que os existentes
bastam.

---

## O que esta baseline não cobre

Registrado para não virar falsa sensação de medida:

- **Carga sustentada.** Todas as medições são rajadas curtas. Não há teste de
  carga contínua, e ele não pertence a esta fase.
- **Latência de rede real.** O banco está na mesma máquina. Com Neon, a latência
  de ida e volta domina o número.
- **Build Release.** As medições são em Debug, que é o que o CI executa nos
  testes.
- **Latência com tabelas grandes.** A Fase 12 mediu o **plano** com 4.000
  linhas e confirmou que o índice é escolhido, mas não cronometrou latência
  nesse volume — e cronometrar seria medir o cache desta máquina.
- **Carga sustentada continua fora.** Os quatro cenários da Fase 12 são rajadas
  de segundos. Um teste de carga contínua mediria o comportamento do pool de
  conexões e do coletor de lixo, que é outra pergunta.
