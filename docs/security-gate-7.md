# Security Gate 7 — Casos e investigação

Data da execução: **2026-09-05**
Escopo: a primeira fase em que pessoas escrevem no sistema.

Testes em `tests/CentralAntifraude.IntegrationTests/SecurityGate7Tests.cs`,
`CasosTests.cs` e `tests/CentralAntifraude.UnitTests/Dominio/CasoTests.cs`.

---

## A premissa desta fase

Até aqui os gates cuidavam sobretudo de leitura e de **ausência** de escrita.
Agora existe escrita de verdade — título, nota, atribuição, veredito — e com
ela três riscos que nenhuma fase anterior teve:

1. **texto escrito por gente**, que é por onde XSS entra;
2. **duas pessoas ao mesmo tempo**, que é por onde o lost update entra;
3. **estado que não pode voltar atrás**, porque a Fase 9 vai usar o resultado
   humano como verdade apurada.

---

## Itens exigidos pelo `ROADMAP.md` (seção 7.10)

| # | Item | Resultado | Teste |
|---|---|---|---|
| 1 | Resolução por usuário sem perfil | ✅ `403` | `Auditor_nao_alcanca_nenhuma_rota_de_escrita` |
| 2 | Caso cross-tenant | ✅ `404` em leitura e ação | `Caso_de_outro_tenant_nao_existe_daqui` |
| 3 | Alerta de tenant B em caso A | ✅ `404`, e o alerta de B fica intacto | `Alerta_de_outro_tenant_nao_entra_em_um_caso` |
| 4 | Mass assignment de status | ✅ 6 campos, todos `400` | `Campo_interno_no_corpo_da_abertura_e_recusado` |
| 5 | Atribuição para usuário de outro tenant | ✅ `404` | `Nao_da_para_atribuir_um_caso_a_usuario_de_outro_tenant` |
| 6 | XSS em nota | ✅ 4 formas, todas `400`, nada gravado | `Nota_com_marcacao_e_recusada_e_nada_e_gravado` |
| 7 | Lost update | ✅ sequencial e **simultâneo** | `Agir_com_uma_versao_velha_e_recusado` e `Duas_resolucoes_simultaneas_produzem_um_resultado_so` |
| 8 | Alteração de caso resolvido | ✅ 5 ações, todas `409` | `Caso_resolvido_recusa_toda_alteracao` |
| 9 | Auditor read-only | ✅ lê tudo, escreve nada | `Auditor_le_o_caso_inteiro` e o teste do item 1 |

---

## 1 e 9. O Auditor

Ele **lê o caso inteiro** — timeline, notas, alertas, sinais. Consultar
decisões e trilha é exatamente o trabalho dele (`CLAUDE.md` seção 8.3).

E não alcança nenhuma das seis rotas de escrita: abrir, assumir, transferir,
anotar, associar alerta, resolver. Todas devolvem `403`, e o caso é lido de
volta do banco depois para confirmar que a versão não mudou.

A lista `acoesPermitidas` que o servidor devolve para ele vem **vazia** — a
tela não precisa saber a regra para deixar de oferecer o botão, e o backend
recusa de qualquer forma.

Um item a mais, fora do ROADMAP: **o Analista não transfere caso**. Assumir é
pegar um caso livre para si; transferir é tirar o caso de alguém, e isso é ato
de supervisão. Sem a separação, qualquer analista poderia esvaziar a fila de
outro.

---

## 2, 3 e 5. Isolamento

| Cenário | Resultado |
|---|---|
| Ler caso do outro tenant | `404` — e não `403`, que confirmaria a existência |
| Agir sobre caso do outro tenant | `404` |
| Lista do outro tenant | não inclui o caso |
| Abrir caso com alerta do outro tenant | `404`, e o alerta de B **continua `Aberto` e sem caso** |
| Associar alerta do outro tenant a caso já aberto | `404` |
| Transferir para usuário de outro tenant | `404` |
| Transferir para um Auditor | `409` — criaria um caso que ninguém pode resolver |
| Sem autenticação, ou com chave de integração | `401` |

O terceiro item é o vazamento mais silencioso da fase: o dado de um cliente
entrando na investigação de outro, sem erro nenhum no caminho. Por isso o teste
não para na resposta HTTP — ele confere **no banco** que o alerta de B ficou
como estava.

O domínio recusa a mesma coisa uma segunda vez: `Caso.AssociarAlerta` compara
as organizações. Se a busca por identificador um dia deixasse de filtrar por
tenant, o agregado ainda recusaria.

---

## 4. Mass assignment

Seis campos internos tentados no corpo da abertura, todos recusados com `400`:

```text
status   resultado   responsavelId   organizacaoId   versao   abertoPorId
```

O JSON estrito é quem recusa: campo desconhecido não é ignorado em silêncio
(`CLAUDE.md` seções 53 e 54). Ignorar faria quem mandou acreditar que o campo
foi aceito.

E **não existe rota que grave o caso inteiro**: `PUT`, `PATCH` e `DELETE` em
`/api/casos/{id}` devolvem `404` ou `405`. Um `PUT` aceitando o objeto todo
convidaria justamente ao mass assignment — bastaria mandar `status` e
`resultado`. As transições são rotas de ação, cada uma com sua regra.

`versao` está no corpo de toda alteração e **não é mass assignment**: ela nunca
é atribuída, só comparada.

---

## 6. Texto escrito por gente

Quatro formas de XSS em nota, todas `400`, e nada gravado:

| Caso | Caso |
|---|---|
| `<script>fetch(...document.cookie)</script>` | `<img src=x onerror=alert(1)>` |
| `<iframe src=javascript:alert(1)>` | `</textarea><svg onload=alert(1)>` |

Título com marcação também é recusado.

**Recusar, e nunca limpar.** Uma nota que o sistema altera sozinho deixa de ser
o que o analista escreveu — e numa investigação isso é pior do que uma recusa
com explicação.

A regra é estreita: `<` seguido de letra ou barra. Um teste prova que
`valor < 100 & custo 5 > 3` passa e **volta do servidor exatamente como foi
escrito** — sem escape no meio do caminho, que faria a nota exibida diferir da
nota gravada.

A defesa de verdade é a saída: a tela renderiza texto, nunca
`dangerouslySetInnerHTML`, e há teste de frontend que confirma que marcação
vinda do servidor não vira elemento.

---

## 7. Lost update, das duas formas

**Sequencial.** Duas notas com a mesma versão: a segunda recebe `409` com
código `versao_desatualizada`. É a checagem da aplicação, e ela existe para dar
um conflito claro sem tocar no banco.

**Simultâneo — a prova de verdade.** Duas resoluções disparadas em paralelo com
a mesma versão, uma pedindo `FraudeConfirmada` e a outra `Legitima`. As duas
passam juntas pela checagem da aplicação; quem arbitra é o banco.

Resultado exigido: exatamente **um `200` e um `409`**, o caso com **um**
resultado e **um** veredito por transação.

Sem a segunda camada, os dois `UPDATE` passariam e o último venceria em
silêncio — dois vereditos diferentes sobre a mesma investigação.

---

## 8. Caso resolvido não muda

Cinco ações tentadas sobre um caso concluído — anotar, associar alerta,
assumir, transferir, resolver de novo — todas `409`. Depois o caso é lido do
banco: mesmo resultado, mesma versão.

Duas delas são tentadas com perfil de **Supervisor**, para que a recusa não
possa ser confundida com falta de permissão.

A razão é o que o caso alimenta: a Fase 9 usa o resultado humano como verdade
apurada. Um veredito que muda depois faria backtests antigos passarem a mentir.

---

## Itens adicionais verificados

| Item | Resultado | Onde |
|---|---|---|
| Resultado fora do vocabulário | ✅ 5 valores, todos `400`; nunca cai num padrão | `Resultado_fora_do_vocabulario_e_recusado` |
| Filtro forjado na lista | ✅ 5 formas, todas `400` | `Filtro_forjado_na_lista_e_recusado` |
| Alerta em caso sai da fila de trabalho | ✅ `Aberto` → `EmCaso` → `Encerrado` | `Um_alerta_levado_para_um_caso_sai_da_fila_de_trabalho` |
| Um alerta em dois casos | ✅ recusado no domínio | `Alerta_que_ja_esta_em_um_caso_nao_entra_em_outro` |
| Caso sem alerta | ✅ recusado no domínio | `Caso_sem_alerta_e_recusado` |
| Timeline append-only | ✅ sem método de alteração e sem rota | por construção, verificado nos testes de domínio |

---

## Achados desta execução

**1. O EF trata filho novo com chave preenchida como `UPDATE`.** Os
identificadores da timeline são gerados pelo domínio (UUIDv7). Quando o EF
encontrava uma entrada nova dentro de uma coleção de navegação, concluía que a
linha já existia e emitia `UPDATE` — que atingia zero linhas e virava um
**falso conflito de concorrência**. Toda nota falhava com `409`.

O sintoma enganava: a mensagem dizia "o recurso mudou depois que você o abriu",
que é exatamente o que se esperaria de um lost update legítimo. Corrigido
tirando timeline e notas da navegação: o repositório grava as entradas novas
explicitamente, e o agregado só acumula o que a operação produziu.

**2. Horário não ordena uma trilha.** Abrir um caso grava dois eventos no mesmo
instante, e o UUIDv7 não desempata — ele sorteia os bits finais. A timeline
mostrava "alerta associado" antes de "caso aberto" de vez em quando. Uma
história que muda de ordem entre duas leituras deixa de ser prova. Corrigido
com uma sequência por caso, com índice único.

**3. A corrida perdida virava `500`.** Na resolução simultânea, quem perde pode
esbarrar no token de versão **ou** na restrição única do veredito, dependendo
de quem chegou onde primeiro. O segundo caminho não era traduzido, e o cliente
recebia "algo quebrou" quando a resposta certa é "outra pessoa concluiu antes;
recarregue". Encontrado só na suíte completa — isolado, o teste passava.

---

## O que este gate **não** cobre

- **Rate limit nas rotas de caso.** Não há operação cara nem enumerável ali
  hoje; a paginação tem teto rígido. A Fase 11 revisita as superfícies.
- **Anexos em notas.** Não existem, e não estão no escopo da v1 — seriam uma
  superfície de upload inteira.
- **Retenção.** Casos, notas e vereditos não expiram. Política de retenção não
  é assunto da v1, mas fica registrado que as tabelas crescem sem teto.
- **Consulta de auditoria.** A trilha das ações de caso é gravada e testada; a
  tela é a Fase 10.

---

## Resultado

**Security Gate 7: verde.**

26 testes em `SecurityGate7Tests`, 10 em `CasosTests` e 28 unitários de domínio
em `CasoTests`. Nenhum item do ROADMAP seção 7.10 ficou sem verificação
executável.
