# Security Gate 3 — Motor de Risco

Data da execução: **2026-09-04**
Escopo: quem pode influenciar o resultado do motor, e quem não pode.

Testes em `tests/CentralAntifraude.IntegrationTests/SecurityGate3Tests.cs`,
`AvaliacaoDeRiscoTests.cs` e `tests/CentralAntifraude.UnitTests/Dominio/`.

---

## A pergunta desta fase

> Alguém de fora consegue influenciar o resultado do motor de risco?

Score, decisão, peso de regra, limiar e configuração são produzidos pelo
servidor a partir da transação e do histórico. Nenhum deles pode ser escolhido
por quem envia o pedido, nem alcançado por quem está em outro tenant.

---

## Itens exigidos pelo `ROADMAP.md` (seção 3.10)

| # | Item | Resultado | Teste |
|---|---|---|---|
| 1 | Usuário não altera score pelo frontend | ✅ não há rota de escrita | `Nao_ha_rota_que_altere_avaliacao_regra_ou_perfil` |
| 2 | Usuário não envia decisão | ✅ 400 por campo desconhecido | `Campo_interno_no_payload_de_ingestao_e_recusado` |
| 3 | Campos internos rejeitados | ✅ 8 campos testados | idem |
| 4 | Avaliação de tenant A não acessível por B | ✅ 404 | `Avaliacao_do_tenant_A_nao_e_alcancavel_pelo_tenant_B` |
| 5 | Rule config controlado | ✅ tipado e validado | `Configuracao_gravada_e_dado_tipado_sem_expressao_executavel` |
| 6 | Ausência de execução arbitrária | ✅ falha alta | `Regra_adulterada_no_banco_nao_vira_comportamento_novo` |
| 7 | Sem SQL/DSL configurável | ✅ por construção | ver abaixo |

---

## 1–3. O cliente não escolhe o resultado

O contrato de entrada é fechado (`JsonUnmappedMemberHandling.Disallow`). Um
payload que traga `score`, `decisao`, `avaliacao`, `sinais`, `versaoDePerfilId`,
`organizacaoId`, `tenantId` ou `pontos` recebe **400**, e não um campo ignorado
em silêncio.

A diferença importa: ignorar faria o integrador acreditar que o valor foi
aceito.

Além do contrato, há a barreira de construção. `AvaliacaoDeRisco.Registrar` é
`internal` ao Domain: **nenhum código da borda HTTP consegue construir uma
avaliação com um score escolhido**, mesmo que o contrato mudasse. O único
caminho até o banco passa pelo motor.

E a prova completa, no banco:
`Score_enviado_nao_substitui_o_score_calculado` verifica que a linha gravada
tem o score que o motor produziu.

### Não existe rota de escrita de risco

`Nao_ha_rota_que_altere_avaliacao_regra_ou_perfil` tenta sete combinações de
método e caminho — `POST /api/regras`, `PUT /api/regras/{id}`,
`DELETE /api/regras/{id}`, `POST /api/regras/perfil`, `PUT /api/regras/perfil`,
`POST /api/avaliacoes`, `PUT /api/transacoes/{id}/avaliacao` — **autenticado
como Administrador**, e exige 404 ou 405 em todas.

Gestão de regra é a Fase 8, com rascunho, backtest e publicação. Uma rota de
escrita agora seria um caminho para mudar o comportamento do motor sem nenhuma
dessas proteções (`CLAUDE.md` seção 23).

---

## 4. Isolamento entre tenants

Três testes, em três camadas diferentes:

| Camada | O que prova |
|---|---|
| `Avaliacao_do_tenant_A_nao_e_alcancavel_pelo_tenant_B` | Detalhe da transação de A devolve **404** para B — não 403, que confirmaria a existência do identificador (`CLAUDE.md` seção 52) |
| `Listagem_do_tenant_B_nao_mostra_transacao_avaliada_do_tenant_A` | O filtro global cobre a listagem, e não só a busca por id |
| `Cada_tenant_enxerga_o_proprio_catalogo_de_regras` | Os identificadores de regra dos dois tenants não têm interseção |

E um quarto, que é o específico desta fase:

**`Contexto_historico_nao_atravessa_a_fronteira_do_tenant`** — o mesmo
`clienteExternoId` existe nos dois tenants, o tenant A recebe uma rajada de seis
transações, e a primeira transação do tenant B com o mesmo id de cliente sai com
**score 0 e nenhum sinal**.

Este é o vazamento mais silencioso que existiria: nenhum dado de A apareceria na
tela de B, mas o histórico de A estaria mudando as decisões de B.

O motor também recusa, no domínio, avaliar uma transação com o perfil de outra
organização (`Motor_recusa_avaliar_transacao_com_perfil_de_outra_organizacao`).
Sem essa verificação, o resultado sairia plausível — com score e sinais — e
ninguém notaria que as regras vieram de outro cliente.

---

## 5–7. Configuração é dado, nunca código

Não há DSL, SQL configurável nem script de usuário — por construção, e não por
validação:

| Camada | Barreira |
|---|---|
| Tipo | `TipoDeRegra` é um enum fechado; só existem os quatro tipos |
| Contrato | Cada tipo tem um `record` de configuração com campos tipados e `Validar()` própria |
| Publicação | `VersaoDeRegra.Publicar` recusa configuração cujo tipo não bate com o da regra, e chama `Validar()` antes de gravar |
| Execução | O motor procura o evaluator em um dicionário registrado **explicitamente**, sem reflexão |
| Leitura | O `switch` do serializador é a lista fechada; tipo desconhecido **lança** |

O que fica no `jsonb` são números e o nome de um tipo do enum. Nada ali é
interpretado como expressão.

### O teste do pior caso

`Regra_adulterada_no_banco_nao_vira_comportamento_novo` executa um `UPDATE`
direto trocando o tipo gravado por `"RegraInventada"` e verifica que a leitura
seguinte **estoura**, em vez de executar algo que ninguém revisou.

É a simulação de um atacante que já tem escrita no banco. Não impede o dano que
ele causaria de outras formas, mas fecha o caminho de "configuração vira
comportamento".

---

## Itens adicionais verificados nesta fase

Não estão na lista do ROADMAP, mas pertencem ao mesmo risco:

| Item | Resultado | Onde |
|---|---|---|
| Fingerprint de IP não vai para a tela | ✅ | `Detalhe_da_transacao_nao_expoe_o_fingerprint_de_ip`; teste equivalente no frontend |
| Endereço IP não aparece em resposta alguma | ✅ | idem — a resposta é varrida por padrão de IPv4 |
| Instrumento de pagamento só como referência tokenizada | ✅ | `pi_demo_123`; o detector de PAN da Fase 2 continua ativo |
| Explicação de sinal é determinística, nunca gerada por IA | ✅ | `Mesma_entrada_produz_o_mesmo_resultado` compara o texto |
| Avaliação não é recalculada em retry | ✅ | `Retry_devolve_a_avaliacao_original_e_nao_cria_uma_segunda` |
| Uma avaliação por transação sob concorrência | ✅ | `Vinte_requisicoes_simultaneas_produzem_uma_unica_avaliacao` |
| Frontend não replica limiar nem deduz decisão | ✅ | `frontend/src/api/risco.ts` não tem número de limiar; a decisão vem do servidor |

---

## O que este gate **não** cobre

Registrado para não virar falsa sensação de segurança:

- **Autorização por perfil na gestão de regras** — não há gestão ainda. Entra na
  Fase 8, com o Security Gate correspondente.
- **Rate limit nas consultas humanas** — `/api/transacoes` e `/api/regras` não
  têm limite próprio. Não há operação cara nem enumerável ali hoje; a Fase 10
  revisita quando o painel agregar dados.
- **Verificação visual no navegador** — a extensão do Chrome está desconectada
  nesta máquina desde a Fase 2. As telas foram verificadas por teste de
  componente (`PaginaDeTransacao.test.tsx`), não por inspeção visual. Dívida
  registrada, mesma da fase anterior.

---

## Resultado

**Security Gate 3: verde.**

16 testes em `SecurityGate3Tests`, 12 em `AvaliacaoDeRiscoTests`, 42 testes
unitários de regra e motor. Nenhum item do ROADMAP seção 3.10 ficou sem
verificação executável.
