# Baseline de performance — caminho crítico

Medição da Fase 4. **Isto não é um SLA.**

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

## O que esta baseline não cobre

Registrado para não virar falsa sensação de medida:

- **Carga sustentada.** Todas as medições são rajadas curtas. Não há teste de
  carga contínua, e ele não pertence a esta fase.
- **Latência de rede real.** O banco está na mesma máquina. Com Neon, a latência
  de ida e volta domina o número.
- **Build Release.** As medições são em Debug, que é o que o CI executa nos
  testes.
- **Tabelas grandes.** Todos os números vêm de tabelas pequenas. O
  comportamento esperado com volume é *melhor* para conflito serializável (o
  índice fica seletivo) e *estável* para latência (o `LIMIT` já limita), mas
  isso não foi medido.
