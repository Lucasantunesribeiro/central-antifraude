# Security Gate 8 — Gestão e versionamento de regras

Data da execução: **2026-09-06**
Escopo: a primeira fase em que uma pessoa altera o **comportamento do produto**.

Testes em `tests/CentralAntifraude.IntegrationTests/SecurityGate8Tests.cs`,
`GestaoDeRegrasTests.cs`, `tests/CentralAntifraude.UnitTests/Dominio/RegraTests.cs`,
`CatalogoDeTiposDeRegraTests.cs` e
`tests/CentralAntifraude.ArchitectureTests/ImutabilidadeDoPublicadoTests.cs`.

---

## A premissa desta fase

Nas fases anteriores uma pessoa alterava dados operacionais: um caso, uma nota,
um veredito. O estrago de um erro ficava contido naquele registro.

Agora ela altera o motor. Uma regra publicada alcança **toda transação que
entrar depois**, e uma versão de perfil errada muda a decisão de todas elas.

Daí os quatro riscos próprios do gate:

1. **perfil errado publicando regra**;
2. **configuração arbitrária**, porque o catálogo fechado é o que impede o banco
   de virar código executável (`CLAUDE.md` seção 21);
3. **edição do que já foi publicado**, que apagaria a explicabilidade histórica;
4. **duas publicações ao mesmo tempo**, porque a versão vigente é uma só.

---

## Itens exigidos pelo `ROADMAP.md` (seção 8.8)

| # | Item | Resultado | Teste |
|---|---|---|---|
| 1 | Analista publicando regra | ✅ `403` em 9 rotas | `Quem_nao_e_supervisao_nao_alcanca_nenhuma_rota_de_regra` |
| 2 | Auditor editando | ✅ `403` nas mesmas 9 | mesmo teste, segunda linha do `Theory` |
| 3 | Tenant B acessando regra do A | ✅ `404` em leitura e ação | `Regra_de_outro_tenant_nao_existe_daqui` |
| 4 | Edição de versão publicada | ✅ 7 rotas, todas `404`/`405` | `Nao_existe_rota_que_altere_uma_versao_publicada` |
| 5 | Config inválida | ✅ 4 faixas, todas `400` com o campo na mensagem | `Configuracao_fora_da_faixa_e_recusada_com_o_campo_na_mensagem` |
| 6 | Tipo de regra desconhecido | ✅ 4 formas, todas `400` | `Tipo_de_regra_desconhecido_e_recusado` |
| 7 | Payload com campo arbitrário | ✅ na configuração e no corpo, `400` | `Campo_arbitrario_na_configuracao_e_recusado`, `Campo_interno_no_corpo_da_criacao_e_recusado` |
| 8 | Concorrência de publicação | ✅ determinístico e **paralelo** | `Duas_publicacoes_simultaneas_da_mesma_regra_produzem_uma_versao_so`, `Publicacoes_paralelas_nunca_deixam_duas_versoes_de_perfil_com_o_mesmo_numero` |

---

## 1 e 2. Quem pode mudar o motor

Gerir regras é supervisão (`CLAUDE.md` seção 8.2). O Analista opera alertas e
casos; o Auditor lê. Nenhum dos dois alcança as nove rotas de administração —
todas devolvem `403`, e a regra é lida de volta do servidor depois para
confirmar que nem a versão dela nem a do perfil mudaram.

**A leitura continua aberta a todos.** `GET /api/regras` e
`GET /api/regras/perfil` respondem `200` para Analista e Auditor: o analista
precisa das regras vigentes para entender o próprio score, e o auditor para
conferir uma decisão. Fechar a leitura junto com a escrita teria sido simples e
errado.

Um item a mais, fora do ROADMAP: a tela **não pede** a lista administrativa
quando o perfil não administra. Não é segurança — é não provocar um `403`
previsível a cada carregamento, que encheria o log de falhas que não são falhas.

Sem autenticação, todas as rotas respondem `401`.

---

## 3. Isolamento

| Cenário | Resultado |
|---|---|
| Ler regra do outro tenant | `404` — e não `403`, que confirmaria a existência |
| Publicar regra do outro tenant | `404` |
| Ativar/desativar regra do outro tenant | `404` |
| Lista administrativa do outro tenant | não inclui a regra |
| Escrever rascunho em A | perfil de B **não** muda de versão; nenhuma regra de B fica com rascunho |

O último item é o vazamento mais silencioso possível nesta fase: a versão de
perfil de um cliente passar a incluir a regra de outro. O resultado sairia
plausível e ninguém notaria. Por isso o teste não para na resposta HTTP — ele
confere **no banco** que nenhuma regra do tenant B ficou com rascunho.

---

## 4. O que já foi publicado não muda

Sete rotas tentadas, todas `404` ou `405`:

```text
PUT    /api/regras/{id}              PATCH  /api/regras/{id}
DELETE /api/regras/{id}              PUT    /api/regras/{id}/versoes/{versaoId}
DELETE /api/regras/{id}/versoes/{v}  PUT    /api/regras/perfil
DELETE /api/regras/perfil
```

Depois a versão é lida de volta e continua com o peso original.

**Não existe `PUT /api/regras/{id}`** de propósito: um corpo com o objeto
inteiro convidaria ao mass assignment — bastaria mandar `ativa` ou
`numeroDaUltimaVersao`. Cada transição tem rota própria, com a sua regra.

A garantia começa antes do HTTP. `VersaoDeRegra` e `VersaoDePerfilDeRisco` não
têm método de alteração, não têm propriedade gravável de fora e não têm campo
público — e um teste de arquitetura verifica os três, para o dia em que alguém
adicionar um `AjustarPontos` resolvendo um problema legítimo.

---

## 5, 6 e 7. Configurar não é programar

A administração não recebe expressão, SQL nem script. Recebe um tipo do catálogo
fechado e um dicionário de **números nomeados**; um `switch` fechado constrói o
record tipado e o domínio valida.

| Tentativa | Resposta |
|---|---|
| `maximoDeTransacoes: 0` / `1001` | `400`, com `maximoDeTransacoes` na mensagem |
| `janelaEmMinutos: 0` / `5000` | `400`, com `janelaEmMinutos` na mensagem |
| `maximoDeTransacoes: 3.5` | `400` — "aceita apenas número inteiro" |
| `tipo: "RegraSecreta"`, `"1"`, `""` | `400` |
| `tipo: "velocidadeporcliente"` | `400` — a comparação é exata; aceitar variação de caixa faria o vocabulário deixar de ser fechado na prática |
| `configuracao: { ..., "bonusSecreto": 500 }` | `400`, com `bonusSecreto` na mensagem |
| `organizacaoId`, `ativa`, `numeroDaUltimaVersao`, `id`, `criadaEm` no corpo | `400` — JSON estrito |
| `nome: "<script>alert(1)</script>"`, `"Regra <b>forte</b>"`, `"ab"` | `400` |
| `pontos: 0`, `-5`, `101` | `400` |

Três recusas são deliberadas e valem registro:

**Campo desconhecido é recusa, não silêncio.** Aceitar faria quem enviou
acreditar que aquele número foi usado pelo motor.

**Campo faltando também é recusa.** Um padrão escolhido pelo servidor mudaria o
comportamento do motor sem ninguém ter decidido aquilo.

**O nome segue a mesma regra de texto do resto do produto** (Fase 7): marcação é
recusada, nunca limpa em silêncio. O nome aparece na lista, no detalhe e na
trilha de auditoria; a defesa de verdade contra XSS continua sendo a saída — a
tela renderiza texto —, e a validação de entrada é a segunda camada.

**A faixa aparece em dois lugares no backend** — na descrição do campo, que a
tela usa para montar o formulário, e dentro de `ConfiguracaoDeRegra.Validar`,
que é a autoridade. Um teste unitário percorre cada campo de cada tipo e exige
que um passo além de cada extremo seja recusado e que cada extremo seja aceito.
A duplicação vira contrato verificado, em vez de risco.

---

## 8. Concorrência de publicação

**Determinístico.** Dois supervisores com a mesma tela aberta apertam publicar a
**mesma regra**. As duas requisições carregam a mesma versão, então a separação
não depende de quem chegou primeiro no banco: exatamente um `201` e um `409`, e
a regra ganha **uma** versão nova — conferido no banco.

**Paralelo.** Duas publicações de regras **diferentes**, disparadas em threads
distintas. As duas são legítimas, mas calculam o mesmo próximo número de versão
de perfil a partir da mesma vigente.

Aqui a afirmação do teste é o invariante, e não a divisão entre `201` e `409`:
dependendo de quanto as duas requisições se sobrepõem, ora as duas passam em
sequência, ora uma perde a corrida — e **as duas saídas são corretas**. O que
nunca pode acontecer é a numeração duplicar. O teste exige:

- nenhum número de versão de perfil repetido no tenant;
- o perfil vigente avançou exatamente o número de publicações que passaram.

Sem o índice único `(perfil, número)`, as duas gravariam: a versão vigente
dependeria de quem terminou por último, e a outra regra ficaria publicada e
**fora** do perfil em vigor, sem ninguém perceber.

**Limiares.** Duas publicações de limiares com o mesmo
`numeroDaVersaoVigente`: a segunda recebe `409` `versao_desatualizada`, e os
limiares que valem são os da primeira.

---

## Itens adicionais verificados

| Item | Resultado | Onde |
|---|---|---|
| Perfil sem regra nenhuma | ✅ desativar a última é `409` | `Desativar_a_ultima_regra_do_perfil_e_recusado` |
| Limiares invertidos | ✅ `409`; os limiares em vigor não mudam | `Limiares_invertidos_sao_recusados` |
| Nome repetido no tenant | ✅ `409` `nome_em_uso` | `Nome_repetido_na_mesma_organizacao_e_recusado` |
| Publicar sem rascunho | ✅ `409` | `Publicar_sem_rascunho_e_recusado` |
| Publicar rascunho igual ao publicado | ✅ `409` | `Publicar_um_rascunho_igual_a_versao_em_vigor_e_recusado` |
| Rascunho não altera o motor | ✅ perfil vigente intocado | `Do_rascunho_a_publicacao_a_regra_percorre_o_fluxo_inteiro` |
| Trilha de auditoria | ✅ rascunho, publicação e sucessão do perfil | `A_publicacao_deixa_rastro_de_quem_publicou` |
| **Explicabilidade histórica** | ✅ v1 continua explicando a avaliação depois da v2 | `Transacao_avaliada_na_v1_continua_explicada_pela_v1_depois_da_v2` |

O último é o teste que sustenta a promessa central do produto: sem ele, uma
auditoria de três meses atrás leria a decisão antiga com os números de hoje — e
a explicação sairia plausível e falsa.

---

## Achados desta execução

**1. A versão de perfil criada ao publicar uma regra não deixava rastro
próprio.** A trilha registrava `VersaoDeRegraPublicada` na regra, e a sucessão
do perfil só era auditada quando alguém mexia nos limiares. Quem perguntasse
"quando o perfil em vigor mudou?" não veria as trocas causadas por publicação ou
desativação de regra — que são a maioria delas. Corrigido movendo a auditoria
para dentro da própria sucessão do perfil, com o motivo em cada registro.

**2. O primeiro teste de concorrência não testava concorrência.** Duas
publicações de regras diferentes disparadas com `Task.WhenAll` sobre o mesmo
`HttpClient` executavam em sequência: as duas passavam, e a asserção "1 criado,
1 conflito" falhava por um motivo que não era defeito do produto. Reescrito em
dois testes — um determinístico, sobre a mesma regra, e um em paralelo real,
afirmando o invariante em vez da divisão de respostas.

**3. `OrderBy(Tipo)` deixou de ser uma ordem total.** Permitir duas regras do
mesmo tipo tornou instável a ordem de execução do motor e a ordem dos sinais
exibidos. O score não mudaria — soma não depende de ordem —, mas a promessa de
determinismo do ADR 0008 sim: duas leituras da mesma avaliação poderiam trazer
os sinais em ordens diferentes. Corrigido com `ThenBy(RegraId)` nos quatro
pontos que ordenam sinais ou regras.

**4. Salvar o rascunho sem tocar em nada enviava configuração vazia.** No editor
da tela da regra, o estado local dos campos nascia vazio e só era preenchido
depois que o contrato do tipo chegava do servidor. Quem abrisse a regra e
clicasse em "Salvar rascunho" mandaria `{}` e receberia `400` por campo
faltando, sem entender por quê. Corrigido enviando os valores efetivamente
exibidos.

**5. A migration gerada pelo scaffold desativaria todas as regras
existentes.** O EF sugeriu `ativa` com `DEFAULT FALSE`; aplicada assim, toda
organização já provisionada ficaria com quatro regras desligadas, e a primeira
publicação de perfil seria recusada por não haver regra nenhuma. Reescrita com
colunas anuláveis, `UPDATE` de preenchimento e só então `NOT NULL`.

---

## O que este gate **não** cobre

- **Backtest antes de publicar.** O `CLAUDE.md` seção 23 coloca o backtest entre
  rascunho e publicação; ele é a Fase 9. Até lá, o rascunho é a única etapa de
  revisão.
- **Rate limit nas rotas de regra.** Não há operação cara nem enumerável ali, e
  o perfil de supervisão já é estreito. A Fase 11 revisita as superfícies.
- **Aprovação de dois supervisores.** Publicar é ato de uma pessoa só. Um
  segundo par de olhos seria uma decisão de produto, e não está no ROADMAP.
- **Retenção de versões.** Versões de regra e de perfil nunca são apagadas — e
  não devem ser, porque explicam avaliações antigas. As tabelas crescem sem
  teto.
- **Consulta de auditoria.** A trilha das publicações é gravada e testada; a
  tela é a Fase 10.

---

## Resultado

**Security Gate 8: verde.**

32 casos em `SecurityGate8Tests`, 13 em `GestaoDeRegrasTests`, 23 unitários em
`RegraTests`, 23 em `CatalogoDeTiposDeRegraTests` e 7 de arquitetura em
`ImutabilidadeDoPublicadoTests` — 98 ao todo. Nenhum item do ROADMAP seção 8.8
ficou sem verificação executável.
