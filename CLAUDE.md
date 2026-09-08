# CLAUDE.md — Central Antifraude

> Governança permanente do projeto **Central Antifraude**.
>
> Este arquivo define o produto, as decisões arquiteturais, as invariantes de domínio, as regras de segurança, qualidade e autonomia do agente.
>
> O `ROADMAP.md` define **quando** cada capacidade será construída.  
> Este `CLAUDE.md` define **como** o projeto deve ser pensado e quais regras não podem ser violadas.

---

## 1. Regra de leitura obrigatória

Antes de qualquer alteração no projeto:

1. Leia este `CLAUDE.md` por completo.
2. Leia o `ROADMAP.md` por completo, quando ele existir.
3. Identifique a fase atualmente autorizada.
4. Inspecione o código e os testes existentes antes de alterar comportamento.
5. Respeite decisões já registradas e não reabra arquitetura sem motivo concreto.

Nenhuma implementação deve começar assumindo que o código atual está vazio ou que decisões anteriores podem ser ignoradas.

---

## 2. Ordem de precedência

Em caso de dúvida, use esta ordem:

1. instrução explícita mais recente do usuário;
2. este `CLAUDE.md`;
3. `ROADMAP.md`;
4. decisões registradas em documentação arquitetural;
5. contratos e invariantes existentes no código;
6. implementação atual.

Se houver conflito real entre os itens 2 a 5 e não for possível resolvê-lo com segurança, pare e explique o conflito antes de alterar o comportamento.

---

## 3. Filosofia do projeto

A Central Antifraude é um projeto de portfólio com qualidade de produto real.

A regra de ouro é:

> **Toda tecnologia, abstração ou complexidade deve resolver um problema concreto do domínio.**

Não adicionar tecnologia apenas para tornar o stack mais impressionante.

Antes de qualquer decisão relevante, pergunte:

> Isso resolve um problema real da Central Antifraude ou só deixa a arquitetura mais sofisticada?

Se a resposta for apenas “fica mais completo”, “é mais moderno” ou “fica melhor no currículo”, não usar.

---

# PARTE I — PRODUTO

## 4. Definição do produto

A **Central Antifraude** é:

> Uma plataforma B2B para avaliação de risco, monitoramento e investigação de transações suspeitas em pagamentos digitais.

O produto recebe uma tentativa de transação, avalia sinais de risco com regras explicáveis, produz uma decisão e disponibiliza ferramentas para investigação humana dos casos suspeitos.

Fluxo conceitual:

```text
Transação recebida
        ↓
Validação
        ↓
Idempotência
        ↓
Persistência
        ↓
Contexto histórico
        ↓
Motor de risco
        ↓
Sinais + Score
        ↓
Decisão
        ↓
PERMITIR / REVISAR / BLOQUEAR
        ↓
Evento
        ↓
Alerta / Caso
        ↓
Investigação humana
        ↓
Resultado da investigação
```

---

## 5. Problema central

A Central Antifraude responde à pergunta:

> **Dada uma tentativa de pagamento digital, qual é o risco dessa transação, por quê e qual ação de risco deve ser recomendada?**

O produto deve permitir que uma equipe de fraude:

- receba transações;
- identifique sinais suspeitos;
- compreenda por que uma transação recebeu determinado risco;
- priorize alertas;
- investigue casos;
- registre decisões humanas;
- audite tudo que ocorreu;
- avalie o impacto histórico de regras antes de publicá-las.

---

## 6. O que o produto NÃO faz

A Central Antifraude **não processa dinheiro**.

Não é:

- banco digital;
- gateway de pagamentos;
- adquirente;
- emissor;
- carteira digital;
- core banking;
- implementação de PIX;
- sistema de autorização financeira;
- sistema de captura;
- sistema de liquidação;
- plataforma completa de chargeback;
- plataforma completa de KYC;
- plataforma completa de AML;
- screening completo de PEP/sanções;
- implementação de 3-D Secure;
- plataforma de device fingerprint comercial;
- sistema bancário homologado;
- solução certificada de compliance.

Nunca descrever o produto como se executasse ou substituísse essas funções.

---

## 7. Recorte funcional da v1

A v1 é especializada em:

> **pagamentos digitais de e-commerce / card-not-present em contexto simulado.**

A aplicação recebe dados de uma tentativa de pagamento já tokenizados ou referenciados por um sistema externo.

O projeto não precisa modelar a rede financeira completa.

---

## 8. Usuários

Perfis principais:

### 8.1 Analista de Fraude

Responsável por:

- analisar alertas;
- assumir casos;
- visualizar sinais;
- consultar histórico;
- registrar notas;
- resolver investigações.

É o principal usuário operacional do produto.

### 8.2 Supervisor de Fraude

Responsável por:

- acompanhar operação;
- gerir regras;
- executar backtests;
- publicar versões de regras;
- supervisionar decisões e filas.

### 8.3 Auditor

Responsável por:

- consultar decisões;
- consultar histórico;
- consultar trilha de auditoria;
- verificar explicabilidade e rastreabilidade.

Perfil essencialmente de leitura.

### 8.4 Administrador

Responsável por:

- usuários;
- permissões;
- organização;
- integrações;
- credenciais.

---

## 9. Multi-tenancy

A Central Antifraude é um SaaS B2B multi-tenant.

Cada organização possui:

- usuários;
- integrações;
- transações;
- regras;
- perfis de risco;
- alertas;
- casos;
- auditoria;
- backtests.

Dados de um tenant nunca podem ser acessados por outro.

`TenantId` nunca deve ser aceito do payload do cliente como fonte de autoridade.

O tenant deve ser derivado da identidade autenticada ou da credencial da integração.

---

## 10. Decisões de risco

Existem exatamente três decisões de risco na v1:

- `Permitir`
- `Revisar`
- `Bloquear`

Estas decisões representam **recomendação de risco**, não resultado financeiro.

Não usar os termos abaixo como substitutos:

- autorizada;
- capturada;
- liquidada;
- processada;
- paga.

Esses termos pertencem ao processamento financeiro, que está fora de escopo.

---

## 11. Resultado da investigação

Uma investigação resolvida pode ser classificada como:

- `FraudeConfirmada`
- `Legitima`
- `Inconclusiva`

O resultado da investigação humana é diferente da decisão automática de risco.

Exemplo:

```text
Decisão de risco: REVISAR
Resultado da investigação: LEGÍTIMA
```

Isso é esperado e representa um falso positivo legítimo do motor.

---

## 12. Workflow de casos

Estados principais:

```text
Novo
 ↓
EmAnalise
 ↓
Resolvido
```

Não transformar a aplicação em um sistema genérico de tickets.

Novos estados só podem ser adicionados quando houver necessidade operacional clara.

---

# PARTE II — DOMÍNIO

## 13. Conceitos centrais

Os conceitos principais são:

- Organização / Tenant
- Usuário
- Integração
- Transação
- Avaliação de Risco
- Sinal de Risco
- Regra
- Versão de Regra
- Perfil de Risco
- Versão do Perfil
- Alerta
- Caso
- Evento de Caso
- Resultado de Investigação
- Backtest
- Evento de Domínio/Integração
- Outbox
- Inbox / Evento Processado
- Auditoria

Evitar entidades genéricas que não representem conceitos reais do domínio.

---

## 14. Transação

Uma transação representa uma tentativa de pagamento recebida pela Central Antifraude.

Ela deve possuir, conforme o estágio do projeto:

- identificador interno;
- tenant;
- identificador externo;
- origem/integração;
- valor;
- moeda;
- horário em que ocorreu;
- horário em que foi recebida;
- referências externas necessárias;
- identificador do cliente externo;
- fingerprint controlado de dispositivo;
- fingerprint controlado de IP quando aplicável;
- país ou informações derivadas necessárias;
- metadados estritamente permitidos pelo contrato.

Não criar um modelo de “cartão bancário”.

---

## 15. Tempos de uma transação

Diferenciar obrigatoriamente:

- `OccurredAt` — quando o evento aconteceu no sistema de origem;
- `ReceivedAt` — quando chegou à Central Antifraude;
- `EvaluatedAt` — quando a avaliação foi concluída.

Nunca tratar estes horários como equivalentes.

Regras temporais devem especificar explicitamente qual timestamp utilizam.

---

## 16. Eventos atrasados

Eventos podem chegar atrasados ou fora de ordem.

Uma transação atrasada:

- deve ser persistida corretamente;
- deve ser avaliada conforme a semântica definida;
- não deve modificar silenciosamente decisões históricas já congeladas;
- pode aparecer em backtests ou análises posteriores.

Não recalcular automaticamente avaliações passadas apenas porque um evento mais antigo chegou depois.

---

## 17. Avaliação de risco

Cada transação possui uma avaliação de risco persistida e explicável.

A avaliação deve registrar informação suficiente para responder:

> Por que esta decisão foi tomada naquele momento?

Deve incluir, conforme a evolução do projeto:

- score final;
- decisão;
- versão do perfil de risco;
- versão do motor quando relevante;
- regras acionadas;
- versões das regras;
- sinais produzidos;
- contribuição de score de cada sinal;
- instante da avaliação.

Avaliações históricas não dependem da configuração atual para serem explicadas.

---

## 18. Sinais de risco

Um sinal é uma evidência produzida por uma regra.

Exemplos de famílias permitidas:

- valor;
- velocidade;
- comportamento histórico;
- dispositivo;
- localização;
- identidade contextual;
- instrumento referenciado;
- rede/IP;
- repetição.

Sinais devem ser:

- explicáveis;
- determinísticos;
- rastreáveis até a regra que os produziu.

Não gerar sinais por IA.

---

# PARTE III — MOTOR DE RISCO

## 19. Motor determinístico

O motor principal de risco é determinístico.

A mesma entrada, mesmo contexto histórico relevante e mesma versão de regras devem produzir o mesmo resultado.

IA não participa do cálculo do score.

IA não pode:

- adicionar pontos;
- remover pontos;
- criar sinal;
- bloquear;
- permitir;
- revisar;
- publicar regra;
- resolver caso.

---

## 20. Score

Modelo inicial:

```text
Score inicial = 0

Cada regra acionada:
  adiciona uma contribuição de risco

Score final:
  limitado ao intervalo 0..100
```

Os thresholds pertencem ao perfil de risco do tenant.

Exemplo conceitual:

```text
0..39   → Permitir
40..69  → Revisar
70..100 → Bloquear
```

Esses valores são defaults fictícios do produto.

Nunca apresentá-los como padrão de mercado ou recomendação oficial.

---

## 21. Tipos de regra

Não criar DSL própria, SQL configurável ou scripts arbitrários de usuário.

O sistema deve possuir um catálogo fechado e tipado de tipos de regra.

Exemplos conceituais:

- `VelocidadePorCliente`
- `NovoDispositivo`
- `ValorAcimaDoHistorico`
- `DivergenciaGeografica`
- `ContaRecente`
- `RepeticaoDeValor`
- `IpEmListaDeRisco`

Cada tipo possui:

- contrato de configuração;
- validação;
- evaluator C# específico;
- versão explícita quando necessário.

Nunca executar código arbitrário vindo do banco.

---

## 22. Regras antifraude e fontes

Não inventar alegações de fraude, segurança, compliance ou comportamento financeiro como se fossem fatos de mercado.

Quando uma regra for apresentada como baseada em prática real:

- pesquisar fonte confiável;
- priorizar documentação oficial;
- documentar a fonte quando relevante.

Pesos, limites e thresholds do produto podem ser fictícios, desde que claramente tratados como configuração de demonstração.

Se uma regra depender de uma afirmação externa material que não possa ser confirmada com segurança, parar e explicar.

---

## 23. Versionamento de regras

Regra publicada é imutável.

Fluxo:

```text
Rascunho
   ↓
Backtest
   ↓
Publicada
   ↓
Substituída por nova versão
```

Depois de publicada:

- não editar configuração;
- não editar peso;
- não editar condição;
- não sobrescrever comportamento histórico.

Qualquer alteração cria nova versão.

---

## 24. Versionamento do perfil de risco

Perfis de risco publicados também são versionados.

Uma avaliação deve apontar para a versão exata do perfil usada.

Publicar uma nova versão não altera avaliações anteriores.

---

## 25. Backtest

Backtest aplica uma regra ou perfil candidato sobre dados históricos sem alterar produção.

Princípios:

- usar o mesmo motor de risco da avaliação real;
- não duplicar lógica de regra;
- não sobrescrever avaliações existentes;
- não criar decisões operacionais reais;
- não gerar alertas reais;
- produzir um resultado próprio de simulação;
- distinguir claramente simulação de produção.

O backtest pode comparar resultados históricos conhecidos, como:

- fraude confirmada;
- legítima;
- inconclusiva.

Não transformar a saída em alegação estatística que os dados não suportem.

---

# PARTE IV — ARQUITETURA

## 26. Arquitetura principal

Arquitetura oficial:

> **Monólito modular com processamento assíncrono orientado a eventos onde houver necessidade real.**

Não criar microserviços sem nova decisão arquitetural explícita.

Módulos conceituais esperados:

- Identidade e Organizações
- Integrações
- Transações
- Risco
- Regras
- Alertas
- Investigações
- Backtests
- Auditoria

Os limites finais de módulos podem evoluir, mas mudanças estruturais relevantes devem preservar coesão e evitar dependências circulares.

---

## 27. Stack base

Backend:

- C#
- .NET 10
- ASP.NET Core
- Entity Framework Core

Frontend:

- React
- TypeScript
- Vite

Banco:

- PostgreSQL
- Neon para ambiente hospedado, salvo nova decisão justificada

Infraestrutura assíncrona:

- Amazon SQS Standard
- AWS Lambda quando fizer sentido para workers
- AWS SSM Parameter Store para secrets/configurações adequadas
- CloudWatch para logs/métricas quando executando em AWS

Frontend hospedado:

- Vercel

Não adicionar serviços AWS apenas para aumentar o número de tecnologias do projeto.

---

## 28. Caminho crítico síncrono

A avaliação principal de risco é síncrona.

Fluxo esperado:

```text
requisição
  ↓
autenticação da integração
  ↓
validação
  ↓
idempotência
  ↓
persistência
  ↓
consulta de contexto
  ↓
avaliação
  ↓
persistência do resultado
  ↓
outbox
  ↓
commit
  ↓
resposta
```

Não colocar SQS entre a requisição e a decisão de risco.

O cliente precisa receber `Permitir`, `Revisar` ou `Bloquear` na própria operação.

---

## 29. Processamento assíncrono

Usar processamento assíncrono para efeitos que não precisam bloquear a resposta principal.

Exemplos:

- criação de efeitos operacionais derivados;
- alertas;
- projeções;
- métricas internas;
- backtests;
- tarefas de recuperação;
- integrações futuras que não pertençam ao caminho crítico.

---

## 30. Filas

Decisão inicial:

- SQS Standard para eventos operacionais;
- fila separada para backtests;
- DLQ por fluxo relevante.

Não usar uma fila única para workloads com características muito diferentes.

Backtests não podem bloquear processamento operacional.

---

## 31. Por que SQS Standard

O sistema deve funcionar corretamente com:

- entrega duplicada;
- atraso;
- retries;
- processamento fora de ordem quando a semântica permitir.

Não depender de FIFO para mascarar problemas de idempotência.

Não assumir exactly-once delivery.

Modelo esperado:

> **at-least-once + consumidores idempotentes.**

---

# PARTE V — IDEMPOTÊNCIA E CONCORRÊNCIA

## 32. Idempotência é requisito de domínio

A mesma transação pode chegar:

- duas vezes;
- simultaneamente;
- com retry após timeout;
- com nova chave de idempotência;
- atrasada.

O sistema não pode produzir duplicidade de:

- transação;
- avaliação;
- alerta;
- efeito de domínio.

---

## 33. Chave de idempotência

Integrações devem utilizar uma chave de idempotência para operações que exigem essa proteção.

Escopo mínimo esperado:

```text
Tenant + Integração + IdempotencyKey
```

Também deve existir proteção de negócio para o identificador externo:

```text
Tenant + Origem/Integração + ExternalTransactionId
```

A garantia final deve existir no banco por constraint única.

Nunca confiar apenas em:

```text
SELECT
if (!exists)
    INSERT
```

---

## 34. Semântica de replay

Comportamento esperado:

### Mesma idempotency key + mesmo payload

Retornar o resultado original.

### Mesma idempotency key + payload diferente

Rejeitar como conflito.

### Mesmo external transaction id + conteúdo equivalente

Retornar ou reconhecer o recurso original conforme contrato.

### Mesmo external transaction id + conteúdo conflitante

Rejeitar como conflito.

A comparação de payload deve ser determinística e explicitamente definida.

---

## 35. Concorrência na avaliação

Regras de velocidade e contexto histórico podem ser afetadas por transações simultâneas.

A operação crítica deve preservar consistência entre:

- persistência da transação;
- leitura do contexto relevante;
- avaliação;
- persistência do resultado;
- criação do evento de outbox.

Decisão inicial:

> usar transação PostgreSQL `SERIALIZABLE` na operação crítica quando necessário para preservar as invariantes.

---

## 36. Retry de serialização

Retry de concorrência deve ser deliberado.

Em falhas de serialização conhecidas, como PostgreSQL `40001`:

1. rollback;
2. reiniciar a operação completa;
3. reler o estado;
4. reexecutar a avaliação;
5. limitar o número de tentativas.

Não reexecutar apenas o comando SQL que falhou.

Não usar retry genérico para qualquer exceção.

---

## 37. Retry não é padrão universal

Nunca adicionar política global de retry sem compreender:

- idempotência;
- transação;
- tipo da falha;
- possibilidade de efeitos duplicados.

Não fazer retry automático de:

- erro de validação;
- conflito de negócio;
- autenticação;
- autorização;
- payload inválido;
- falha determinística de regra.

---

# PARTE VI — OUTBOX, EVENTOS E CONSUMIDORES

## 38. Transactional Outbox

Toda publicação de evento que depende de uma alteração persistida deve evitar dual-write inconsistente.

Padrão:

```text
BEGIN

alteração de domínio
avaliação
sinais
OutboxEvent

COMMIT
```

A gravação do estado e a gravação do evento de outbox fazem parte da mesma transação.

---

## 39. Publicação do Outbox

Um dispatcher publica eventos pendentes.

Ele deve:

- processar em lotes pequenos;
- suportar concorrência;
- não assumir entrega única;
- marcar publicação apenas após sucesso;
- possuir mecanismo de recuperação.

Se múltiplos dispatchers forem usados, pode-se empregar locking apropriado como `FOR UPDATE SKIP LOCKED`.

---

## 40. Duplicidade entre Outbox e broker

Se a mensagem for enviada ao broker e o processo cair antes de marcar o outbox como publicado, a mensagem pode ser enviada novamente.

Isso é comportamento esperado.

A correção pertence ao consumidor idempotente, não a uma promessa impossível de exactly-once.

---

## 41. Envelope de evento

Eventos relevantes devem possuir contrato explícito.

Estrutura conceitual:

```json
{
  "eventId": "...",
  "eventType": "TransactionEvaluated.v1",
  "occurredAt": "...",
  "tenantId": "...",
  "correlationId": "...",
  "payload": {}
}
```

Não usar eventos anônimos sem versão.

Mudanças incompatíveis exigem nova versão de contrato.

---

## 42. Consumidores idempotentes

Consumidores devem registrar `EventId` processado ou usar mecanismo equivalente persistente.

Apenas memória local não é suficiente.

Além da Inbox, efeitos importantes devem possuir invariantes próprias.

Exemplo:

```text
Alert.EvaluationId UNIQUE
```

A idempotência deve existir em mais de uma camada quando o domínio justificar.

---

## 43. Ordem de eventos

Nunca presumir ordem global.

Se determinada operação exigir ordem:

- documentar a necessidade;
- modelar versão/estado esperado;
- rejeitar transições inválidas;
- ou escolher mecanismo apropriado com justificativa.

Não migrar toda a infraestrutura para FIFO apenas por um caso local sem analisar alternativas.

---

# PARTE VII — PERSISTÊNCIA

## 44. PostgreSQL como fonte de verdade

PostgreSQL é a fonte de verdade operacional.

Não introduzir outra fonte de verdade sem necessidade concreta.

Redis não é parte da arquitetura inicial.

---

## 45. Redis

Não usar Redis inicialmente.

Só reconsiderar se houver evidência concreta de gargalo que:

- não possa ser resolvido com índice;
- não possa ser resolvido com consulta adequada;
- não possa ser resolvido com modelagem;
- justifique uma nova estratégia de consistência/cache.

Se Redis for proposto futuramente, documentar:

- o dado cacheado;
- TTL;
- estratégia de invalidação;
- comportamento em cache miss;
- fonte de verdade;
- impacto de inconsistência.

---

## 46. Índices

Criar índices a partir de consultas reais.

Exemplos de eixos prováveis:

- `TenantId + CustomerExternalId + OccurredAt`
- `TenantId + DeviceFingerprint + OccurredAt`
- `TenantId + PaymentInstrumentRef + OccurredAt`
- `TenantId + Decision + EvaluatedAt`
- `TenantId + AlertStatus + CreatedAt`

Não criar dezenas de índices preventivamente.

Validar com queries, planos e testes quando necessário.

---

## 47. Migrations

Toda alteração de schema deve:

- possuir migration;
- ser revisável;
- ser reproduzível;
- considerar dados existentes;
- considerar constraints;
- considerar downgrade apenas quando fizer sentido real.

Não alterar banco manualmente em produção sem migration registrada.

---

# PARTE VIII — SEGURANÇA

## 48. Security Gate desde o início

Segurança não é fase final.

Toda fase deve considerar:

- autenticação;
- autorização;
- isolamento de tenant;
- validação;
- exposição de dados;
- mass assignment;
- idempotência;
- replay;
- rate limiting;
- logs;
- secrets.

O `ROADMAP.md` deve incluir Security Gates ao longo do projeto.

---

## 49. Autenticação humana

Usuários humanos devem usar mecanismo próprio de sessão/autenticação.

Diretriz inicial:

- access token curto;
- refresh token;
- refresh rotation;
- detecção de reuse quando aplicável;
- armazenamento seguro do refresh;
- CSRF e Origin quando o desenho de cookie exigir.

A implementação exata deve respeitar o modelo de deploy real do frontend/backend.

---

## 50. Integrações máquina-a-máquina

Integrações externas não devem fingir ser usuários humanos.

Devem possuir credenciais próprias.

A credencial:

- identifica a integração;
- determina o tenant;
- pode ser revogada;
- deve ser armazenada somente em forma segura no servidor;
- nunca deve aparecer em logs.

O payload não escolhe o tenant.

---

## 51. Assinaturas/HMAC

Não adicionar assinatura HMAC de request apenas para parecer mais robusto.

Para uma integração cliente-servidor normal via HTTPS, credencial segura + TLS + idempotência podem ser suficientes para o escopo.

HMAC passa a ser relevante quando houver problema concreto, como webhooks outbound ou requisito específico de autenticidade de payload.

---

## 52. Autorização

Aplicar RBAC no backend.

Nunca confiar em esconder botão no frontend.

Toda operação sensível deve validar:

- identidade;
- tenant;
- perfil;
- ownership/escopo quando aplicável.

Cross-tenant deve retornar comportamento que não revele a existência do recurso.

Preferência:

> `404` para recurso fora do tenant.

---

## 53. Mass assignment

DTOs de entrada devem ser explícitos.

Não bindar entidades persistidas diretamente a payload externo.

Campos internos como:

- `TenantId`;
- score;
- decisão;
- status interno;
- versão;
- flags administrativas;

não podem ser alterados porque chegaram no JSON.

---

## 54. JSON estrito

Contratos importantes devem rejeitar campos desconhecidos quando isso aumentar segurança e previsibilidade.

Não aceitar silenciosamente payload com campos que parecem suportados mas não são.

---

## 55. Rate limiting

Aplicar rate limiting onde existir risco real:

- login;
- refresh;
- criação/rotação de credenciais;
- endpoints públicos de integração;
- operações administrativas caras.

Limites devem ser configuráveis e testáveis.

Não afirmar que um limite é “padrão bancário” sem fonte.

---

## 56. Dados sensíveis

Nunca armazenar:

- PAN real;
- CVV;
- senha bancária;
- credenciais financeiras;
- secrets em texto puro;
- tokens de autenticação em logs.

Preferir minimização.

---

## 57. IP e dispositivo

Quando o valor bruto não for necessário, persistir representação derivada.

Exemplo:

- HMAC/fingerprint do IP;
- país derivado;
- fingerprint de dispositivo fornecido/gerado de forma controlada.

Se IP bruto for temporariamente necessário em alguma fase, documentar:

- por quê;
- tempo de retenção;
- quem pode acessar;
- como é removido.

---

## 58. Instrumento de pagamento

Usar somente referência/token externo.

A Central Antifraude não armazena dados completos de cartão.

Exemplo permitido:

```text
PaymentInstrumentRef = "pi_demo_123"
```

Exemplo proibido:

```text
4111111111111111
123
```

---

## 59. Secrets

Secrets:

- nunca entram no repositório;
- nunca entram em seed;
- nunca entram em screenshot;
- nunca entram em logs;
- devem usar mecanismo apropriado por ambiente.

Em AWS, preferir SSM Parameter Store quando adequado ao custo e ao escopo.

Executar secret scanning no CI.

---

## 60. SSRF

Se futuramente o backend fizer requisição para URLs configuráveis ou fornecidas externamente, tratar SSRF como ameaça explícita.

Não criar integração HTTP genérica sem:

- allowlist quando aplicável;
- bloqueio de endereços locais/metadata;
- validação de esquema;
- redirects controlados;
- timeout.

---

## 61. IA e segurança

Se IA for introduzida:

- entrada deve ser minimizada;
- não enviar secrets;
- não enviar dados financeiros desnecessários;
- saída não é confiável;
- prompt injection deve ser considerada;
- output deve passar por contrato/validação quando utilizado pela aplicação;
- IA não recebe autoridade para decisões críticas.

---

# PARTE IX — IA

## 62. IA é opcional

O produto deve funcionar completamente sem IA.

Não adicionar IA apenas porque o projeto anterior possuía IA.

IA só entra se resolver problema probabilístico real de interface ou produtividade.

---

## 63. Usos permitidos de IA

Exemplos aceitáveis:

- resumir um caso longo;
- resumir timeline;
- explicar sinais já calculados;
- organizar informações para o analista;
- transformar informação existente em texto assistivo.

---

## 64. Usos proibidos de IA

IA não pode ser autoridade para:

- score;
- classificação final;
- bloqueio;
- permissão;
- criação automática de sinal;
- publicação de regra;
- resolução automática de caso;
- alteração autônoma de dados críticos.

A aplicação determinística permanece fonte da decisão.

---

# PARTE X — INVESTIGAÇÃO E AUDITORIA

## 65. Alerta ≠ Caso

Um alerta é um resultado operacional gerado pelo sistema.

Um caso é uma investigação humana.

Um caso pode reunir:

- um ou mais alertas;
- uma ou mais transações;
- timeline;
- notas;
- responsável;
- resultado.

Não modelar caso como mero sinônimo de alerta.

---

## 66. Timeline de investigação

A timeline deve registrar eventos operacionais importantes:

- criação;
- atribuição;
- mudança de estado;
- associação de alerta;
- nota;
- resolução;
- reabertura se futuramente permitida.

Eventos importantes devem registrar autor e horário.

---

## 67. Auditoria

Auditoria é append-only para operações relevantes.

Exemplos:

- criação/alteração de usuário;
- criação/revogação de credencial;
- publicação de regra;
- publicação de perfil;
- mudança administrativa;
- atribuição de caso;
- resolução de caso.

Não criar endpoints normais para editar ou apagar auditoria.

---

## 68. Auditoria não é Event Sourcing

O estado do sistema é persistido normalmente.

A auditoria registra rastreabilidade.

Não reconstruir o sistema inteiro por replay de eventos.

Event sourcing está fora da arquitetura inicial.

---

# PARTE XI — OBSERVABILIDADE

## 69. Correlation ID

Toda operação externa relevante deve possuir `CorrelationId`.

O identificador deve acompanhar:

```text
HTTP
 ↓
domínio
 ↓
outbox
 ↓
mensagem
 ↓
worker
 ↓
efeito
```

Isso deve permitir investigar um fluxo ponta a ponta.

---

## 70. Structured logging

Logs devem ser estruturados.

Preferir propriedades como:

- `CorrelationId`
- `EventId`
- `EvaluationId`
- `Operation`
- `RuleType`
- `Decision`

Evitar inserir conteúdo sensível.

Não logar payload completo por conveniência.

---

## 71. Métricas

Métricas devem possuir baixa cardinalidade.

Exemplos:

- avaliações totais;
- duração de avaliação;
- decisões por categoria;
- eventos processados;
- falhas de processamento;
- tamanho do outbox pendente;
- duração de backtest;
- mensagens em DLQ.

Não usar como dimensão de métrica:

- TransactionId;
- CustomerId;
- CorrelationId;
- TenantId quando gerar cardinalidade desnecessária.

---

## 72. DLQ

Fluxos assíncronos relevantes devem possuir estratégia clara de falha.

Mensagens que falham repetidamente devem ir para DLQ quando aplicável.

Deve existir forma documentada de:

- identificar;
- inspecionar;
- corrigir causa;
- reprocessar de maneira idempotente.

---

# PARTE XII — INFRAESTRUTURA E CUSTO

## 73. AWS é opcional

AWS não é objetivo do projeto.

Usar somente quando resolver:

- processamento assíncrono;
- execução serverless;
- observabilidade;
- secrets;
- scheduling;
- outro problema concreto.

---

## 74. Infraestrutura inicial esperada

Quando implantada na AWS, a arquitetura esperada pode usar:

- Lambda Function URL para a API;
- Lambda para workers;
- SQS Standard;
- DLQ;
- EventBridge Scheduler quando necessário para recuperação/dispatch;
- CloudWatch;
- SSM Parameter Store.

Não usar API Gateway sem necessidade concreta.

---

## 75. Serviços proibidos por padrão

Não introduzir sem nova justificativa arquitetural:

- RDS;
- EC2;
- ECS;
- EKS;
- Kubernetes;
- NAT Gateway;
- DynamoDB;
- Kafka;
- MSK;
- OpenSearch;
- graph database;
- Redis;
- EventBridge Event Bus;
- Step Functions;
- microserviços.

A lista não significa “nunca”.

Significa:

> não usar sem um problema concreto que justifique reabrir a decisão.

---

## 76. Custo esperado

Objetivo:

> **AWS com custo esperado de US$ 0,00 para o uso de portfólio.**

Antes de habilitar qualquer recurso que possa cobrar:

1. verificar pricing atual;
2. estimar uso;
3. estimar pior caso razoável;
4. confirmar que não existe custo fixo;
5. documentar a decisão.

Nunca assumir que free tier é hard cap.

---

## 77. Envelope de uso de portfólio

Referência inicial para dimensionamento:

- ~10.000 avaliações/mês;
- ~10.000 eventos/mês;
- até ~100 backtests/mês;
- dados PostgreSQL abaixo de ~500 MB inicialmente;
- logs controlados;
- sem tráfego artificial de carga contínua em produção.

Esse envelope é referência, não SLA.

---

## 78. Budget e proteção

Ao chegar à fase de deploy:

- configurar budget/alerta de custo quando disponível;
- reduzir retenção de logs quando adequado;
- evitar polling excessivo;
- evitar loops de retry;
- evitar schedulers em frequência desnecessária.

---

# PARTE XIII — FRONTEND E UX

## 79. Direção de UX

A interface deve parecer:

> **software operacional usado por analistas de fraude durante o trabalho.**

Não criar dashboard genérico de portfólio.

Priorizar:

- **uma ação principal óbvia por tela** — quem chega precisa saber o que fazer,
  e não apenas o que está acontecendo;
- **legibilidade antes de densidade**: corpo de 16 px, rótulo de 14 px, alvo de
  clique de pelo menos 40 px de altura;
- **poucos blocos por tela** — o que não couber vai para aba ou seção
  recolhível, e não para mais uma faixa empilhada;
- tabelas, com a **linha inteira clicável** quando ela representa um registro;
- filtros;
- status;
- timeline;
- sinais;
- investigação;
- navegação rápida;
- leitura de dados.

> **Correção registrada em 2026-09-08.** Este item dizia apenas "densidade
> adequada", e densidade virou desculpa para letra miúda e informação
> espalhada: o painel chegou a ter onze blocos, 1.853 caracteres de texto e
> nenhuma ação principal, e a fila de alertas tinha uma única ação na tela
> inteira. A referência visual passou a ser Stripe e Vercel — respiro e ação
> clara — em vez de terminal denso. Cabe menos linha por tela, e a troca é
> deliberada. Ver ADR 0018.

Evitar:

- glassmorphism;
- gradientes decorativos;
- excesso de cards;
- animações inúteis;
- páginas vazias com hero sections;
- visual de landing page dentro do produto.

---

## 80. Telas principais

Escopo esperado da v1:

- Painel Operacional
- Transações
- Detalhe da Transação
- Alertas
- Casos
- Detalhe do Caso
- Regras
- Backtest
- Auditoria
- Administração

O `ROADMAP.md` define quando cada uma será construída.

---

## 81. Regra de negócio no frontend

O frontend não calcula score oficial.

O frontend não decide fraude.

O frontend não replica regra de autorização do backend.

Pode realizar validação de UX, mas o backend é autoridade.

---

# PARTE XIV — TESTES E QUALIDADE

## 82. Filosofia de testes

Número de testes não é objetivo.

Cobrir riscos reais é objetivo.

A suíte deve crescer conforme as invariantes do produto.

---

## 83. Tipos de teste esperados

Conforme as fases:

- unitários;
- domínio;
- integração;
- PostgreSQL real;
- API;
- autorização;
- multi-tenancy;
- concorrência;
- idempotência;
- eventos;
- workers;
- segurança;
- frontend;
- end-to-end quando fizer sentido.

---

## 84. PostgreSQL real em integração

Testes que dependem de semântica PostgreSQL devem usar PostgreSQL real, preferencialmente via Testcontainers.

Não usar SQLite como substituto para testar:

- `SERIALIZABLE`;
- constraints específicas;
- locking;
- `SKIP LOCKED`;
- concorrência;
- comportamento de transação.

---

## 85. Testes de concorrência obrigatórios

O projeto deve demonstrar concorrência real.

Cenários mínimos esperados ao longo do roadmap:

### Mesma transação concorrente

Múltiplas requisições simultâneas para a mesma transação:

> resultado persistido único.

### Regra de velocidade

Transações simultâneas do mesmo cliente:

> avaliação consistente com a semântica definida.

### Evento duplicado

Mesma mensagem processada múltiplas vezes:

> efeito único.

### Outbox duplicado

Publicação duplicada:

> consumidor continua correto.

### Evento fora de ordem

> estado final permanece válido.

---

## 86. Testes de isolamento

Cobrir explicitamente:

- usuário tenant A acessando recurso tenant B;
- integração tenant A tentando enviar tenant B;
- identificadores conhecidos de outro tenant;
- filtros;
- exportações;
- auditoria;
- regras;
- casos.

Nenhuma nova área multi-tenant deve ser considerada pronta sem teste de isolamento.

---

## 87. Testes de versionamento

Cobrir:

```text
Regra v1 publicada
 ↓
transação avaliada
 ↓
Regra v2 publicada
 ↓
consulta histórica
```

A transação antiga deve continuar explicável pela v1.

O mesmo vale para perfil de risco.

---

## 88. Testes de backtest

Garantir:

- backtest não altera avaliação real;
- backtest não cria alerta real;
- backtest não resolve caso;
- motor compartilhado mantém semântica;
- execução repetida segue contrato de idempotência definido.

---

## 89. Produção-like tests

Quando um bug depender de característica de produção, criar teste que reproduza essa característica sempre que razoável.

Lição herdada do projeto anterior:

> bug real deve, quando possível, virar proteção permanente no CI.

---

# PARTE XV — CI/CD

## 90. CI

O CI deve evoluir para incluir:

- restore/install;
- build;
- testes;
- lint/format quando adotado;
- frontend tests;
- backend tests;
- security gates;
- secret scanning;
- migrations validation quando aplicável.

Não perseguir quantidade de checks.

Checks devem proteger riscos reais.

---

## 91. Deploy

Deploy não é autorizado implicitamente por uma fase.

Claude pode preparar:

- infraestrutura;
- workflows;
- scripts;
- documentação;
- manifests.

Mas só pode efetuar deploy quando o usuário autorizar explicitamente.

---

# PARTE XVI — DADOS DE DEMONSTRAÇÃO

## 92. Dados fictícios

Nunca usar PII real.

A demo deve conter dados fictícios intencionais.

Não gerar centenas de registros aleatórios sem narrativa.

---

## 93. Seed com histórias

O seed deve permitir entender o produto.

Exemplos conceituais:

- cliente antigo com comportamento normal;
- valor muito acima do histórico;
- muitas tentativas em curto intervalo;
- dispositivo novo;
- divergência geográfica;
- falso positivo legítimo;
- conjunto relacionado de transações suspeitas.

O usuário da demo deve conseguir entender por que cada alerta existe.

---

## 94. Falso positivo é obrigatório na narrativa

Nem todo alerta deve terminar como fraude.

A demonstração precisa mostrar pelo menos um caso:

```text
Decisão automática: Revisar
Resultado humano: Legítima
```

Isso demonstra por que investigação humana existe.

---

# PARTE XVII — DOCUMENTAÇÃO

## 95. Documentar decisões relevantes

Decisões arquiteturais importantes devem ser registradas.

Exemplos:

- por que síncrono no caminho crítico;
- por que SQS Standard;
- por que Outbox;
- por que consumidores idempotentes;
- por que `SERIALIZABLE`;
- por que sem Redis;
- por que sem microserviços;
- por que regras versionadas.

Não documentar cada classe ou método como ADR.

---

## 96. README final

O README final deverá explicar:

- problema;
- produto;
- demonstração;
- fluxo;
- arquitetura;
- regras explicáveis;
- investigação;
- idempotência;
- concorrência;
- segurança;
- observabilidade;
- testes;
- decisões;
- limitações;
- deploy;
- custos;
- screenshots;
- vídeo curto.

Não escrever README genérico de framework.

---

# PARTE XVIII — PADRÕES DE IMPLEMENTAÇÃO

## 97. Clareza sobre abstração

Preferir código explícito e compreensível.

Não criar:

- abstração para uma única implementação sem necessidade;
- generic repositories por padrão;
- factories vazias;
- mediator apenas para aumentar camadas;
- interfaces sem razão de teste ou arquitetura;
- reflection desnecessária;
- metaprogramação para regras de negócio.

---

## 98. Domínio primeiro

Regra de negócio não deve viver em:

- controller;
- React;
- migration;
- serializer;
- mapper.

Controllers coordenam HTTP.

Domínio/aplicação executam comportamento.

Persistência persiste.

---

## 99. EF Core

Evitar consultas acidentais e N+1.

Selecionar somente dados necessários quando relevante.

Não criar `Include()` indiscriminadamente.

Antes de otimizar:

- medir;
- inspecionar SQL;
- verificar plano quando necessário.

---

## 100. Datas e horários

Persistir instantes em UTC.

Não usar horário local do servidor como autoridade.

Conversões para exibição pertencem à camada apropriada.

`OccurredAt` vindo de integração deve possuir contrato claro de timezone/offset.

---

## 101. Dinheiro

Valores monetários devem usar tipo decimal apropriado.

Nunca usar `float`/`double` para valor financeiro.

A moeda deve estar explícita quando o domínio permitir mais de uma.

---

## 102. Identificadores

Identificadores públicos não devem expor sequência interna sensível quando isso não for necessário.

A escolha concreta de GUID/UUID/ULID será formalizada na fase de fundação técnica se ainda não estiver definida.

Não misturar identificador externo do integrador com identificador interno.

---

## 103. Erros

Erros de API devem possuir contrato consistente.

Distinguir:

- validação;
- autenticação;
- autorização;
- not found;
- conflito;
- rate limit;
- falha interna.

Não vazar stack trace ou detalhes de infraestrutura em produção.

---

# PARTE XIX — AUTONOMIA DO CLAUDE CODE

## 104. Regra principal de execução

> **Uma autorização de fase = autorização para concluir a fase inteira.**

Depois que o usuário autorizar uma fase do `ROADMAP.md`, Claude pode executar todas as atividades necessárias para cumprir os critérios daquela fase.

Não parar após cada microetapa para pedir permissão.

---

## 105. Ações permitidas dentro de uma fase autorizada

Claude pode, sem nova confirmação:

- analisar o repositório;
- criar plano técnico da fase;
- pesquisar documentação;
- implementar;
- refatorar dentro do escopo;
- criar arquivos;
- modificar arquivos;
- criar migrations;
- executar migrations em ambiente local/teste;
- criar testes;
- executar testes;
- corrigir testes;
- executar linters;
- corrigir warnings relevantes;
- executar Security Gates;
- atualizar documentação;
- atualizar o `ROADMAP.md` com progresso factual;
- criar commits locais;
- dividir implementação em commits coerentes.

---

## 106. Quando NÃO parar

Não pedir autorização adicional apenas porque:

- terminou backend e vai começar frontend;
- terminou uma subetapa;
- precisa criar migration;
- precisa adicionar teste;
- encontrou bug da própria implementação;
- precisa corrigir CI;
- precisa atualizar documentação;
- precisa executar teste de integração;
- precisa refatorar código criado na mesma fase;
- uma primeira abordagem falhou e existe alternativa segura dentro da arquitetura aprovada.

Claude deve continuar até concluir a fase ou encontrar uma condição real de parada.

---

## 107. Condições reais de parada

Claude deve parar e explicar quando houver:

### 107.1 Conflito real entre fontes confiáveis

Especialmente em:

- segurança;
- fraude;
- finanças;
- compliance;
- comportamento externo relevante.

### 107.2 Mudança de arquitetura ou stack

Exemplos:

- trocar PostgreSQL;
- adicionar Redis;
- introduzir microserviço;
- adicionar Kafka;
- trocar framework principal;
- mudar modelo de autenticação de forma estrutural.

### 107.3 Operação irreversível

Exemplos:

- apagar dados importantes;
- destruir ambiente;
- migration destrutiva sem estratégia segura;
- reset de produção.

### 107.4 Falta de secret ou credencial necessária

Nunca inventar credencial.

### 107.5 Custo pago ou risco material de cobrança

Não habilitar recurso pago sem autorização.

### 107.6 Deploy ou push não autorizado

Ver regras específicas abaixo.

### 107.7 Requisito de negócio impossível de inferir

Somente quando não existir decisão segura possível dentro do produto definido.

Não usar essa condição para perguntas cosméticas ou microdecisões técnicas reversíveis.

---

## 108. Push

Commits locais são permitidos dentro de fase autorizada.

`git push` exige autorização explícita do usuário.

Não interpretar “conclua a fase” como autorização de push.

---

## 109. Deploy

Deploy para qualquer ambiente remoto exige autorização explícita do usuário, salvo se ele já tiver autorizado claramente aquele deploy específico na mesma solicitação.

Inclui:

- Vercel;
- AWS;
- Neon production migration;
- publicação de Function URL;
- criação de recursos cloud com potencial de custo;
- release pública.

---

## 110. Operações destrutivas

Nunca executar silenciosamente:

- `drop database`;
- apagar ambiente remoto;
- reset de branch;
- force push;
- remover dados persistidos importantes;
- destruir recursos cloud;
- sobrescrever secrets.

---

## 110-A. Comando permanente: “siga para a próxima fase”

Autorização permanente concedida pelo usuário em **2026-09-03**, válida em
**todas as sessões futuras**.

### Gatilho

Quando o usuário disser apenas:

> **siga para a próxima fase**

isso equivale a autorização explícita para **executar integralmente a próxima
fase ainda não concluída do `ROADMAP.md`**.

### Procedimento obrigatório antes de executar

1. ler o `CLAUDE.md` por completo;
2. ler o `ROADMAP.md` por completo;
3. identificar a próxima fase com status `Não iniciada` cuja fase anterior
   esteja concluída;
4. revisar o estado real do repositório e os commits existentes;
5. validar critérios, dependências e Security Gate daquela fase;
6. executar a fase inteira até a conclusão.

### Autonomia concedida

Dentro da fase autorizada, Claude decide **sozinho** toda questão técnica
reversível, segura e compatível com este documento e com o `ROADMAP.md`:

estrutura de classes; nomes internos; organização de pastas; DTOs; validações;
índices; constraints; bibliotecas compatíveis; configuração de testes;
refatorações; estratégia de implementação; tratamento de erros; migrations;
detalhes de UI; divisão de commits; e qualquer correção necessária para deixar
build, testes e CI verdes.

**Não apresentar alternativas para o usuário escolher** quando uma decisão
técnica razoável puder ser tomada. Escolher sempre a opção que, nesta ordem:

1. preserva as invariantes do projeto;
2. resolve o problema real do domínio;
3. é mais simples de manter e explicar;
4. evita overengineering;
5. tem boa cobertura de testes;
6. é segura;
7. é compatível com as decisões já registradas.

### Não parar por

Subetapa concluída; transição backend → frontend; migration; teste novo; erro
de build; teste quebrado; bug encontrado; necessidade de refatorar; preferência
de implementação; escolha de biblioteca compatível; primeira abordagem que
falhou. **Resolver e continuar.**

### Continuam exigindo parada

As condições da seção 107 permanecem integralmente válidas — em especial
`git push`, deploy, recurso remoto, custo pago, operação destrutiva, falta de
secret, mudança de arquitetura/stack e conflito real entre fontes oficiais.

### Encerramento

Ao concluir: atualizar o status no `ROADMAP.md`, manter a árvore de trabalho
coerente, criar commits locais, **não fazer push nem deploy**, e apresentar
resumo com entregas, decisões, builds, testes, Security Gate, commits, débitos
técnicos não bloqueantes e confirmação de que não houve push/deploy.

### Limite

Uma autorização vale para **uma** fase. Concluída a fase, parar e aguardar novo
comando — a seção 10 do `ROADMAP.md` continua valendo.

---

# PARTE XX — PESQUISA

## 111. Fontes

Para afirmações externas materiais, priorizar:

1. documentação oficial;
2. normas/órgãos oficiais;
3. documentação técnica do fornecedor;
4. fontes técnicas primárias;
5. fontes secundárias apenas como apoio.

---

## 112. Não inventar antifraude

Nunca criar justificativa como:

> “bancos usam exatamente score 70 para bloquear”

sem fonte real.

O projeto pode definir:

> “na configuração demo, score >= 70 bloqueia”.

Essa distinção deve ser explícita.

---

## 113. Conflitos de fonte

Se duas fontes oficiais relevantes entrarem em conflito e o comportamento correto afetar o produto:

- não escolher silenciosamente;
- registrar o conflito;
- mostrar as fontes;
- parar a parte afetada;
- aguardar decisão.

---

# PARTE XXI — ESCOPO E CONTROLE DE COMPLEXIDADE

## 114. Não transformar o projeto em plataforma infinita

A v1 deve ser completa dentro do problema definido.

Evitar adicionar módulos adjacentes apenas porque existem em soluções comerciais.

---

## 115. Fora de escopo técnico inicial

Não implementar por iniciativa própria:

- machine learning antifraude;
- graph fraud detection;
- behavioral biometrics;
- fingerprint avançado;
- streaming Kafka;
- data lake;
- warehouse;
- BI externo;
- AML;
- KYC;
- chargeback;
- payment orchestration.

Se uma dessas capacidades for sugerida durante uma fase, avaliar se pertence à v1 antes de implementar.

---

## 116. Performance

Não otimizar prematuramente.

Primeiro:

- corretude;
- segurança;
- idempotência;
- concorrência;
- explicabilidade.

Depois medir.

Performance deve ser testada onde existe risco real, especialmente no caminho de avaliação.

---

# PARTE XXII — DEFINIÇÃO DE PRONTO

## 117. Uma fase só está concluída quando

Todos os itens aplicáveis estiverem verdadeiros:

- implementação concluída;
- build verde;
- testes relevantes verdes;
- migrations válidas;
- segurança da fase validada;
- isolamento de tenant testado quando aplicável;
- warnings relevantes investigados;
- documentação atualizada;
- critérios do `ROADMAP.md` satisfeitos;
- nenhuma regressão conhecida deixada sem registro;
- repositório em estado coerente;
- commits locais criados quando fizer sentido.

Não declarar fase concluída apenas porque “funciona manualmente”.

---

## 118. Bugs encontrados durante a fase

Se um bug estiver dentro do escopo ou tiver sido causado pela implementação da fase:

> corrigir antes de concluir.

Não transformar bugs próprios em “melhoria futura” para encerrar a fase mais rápido.

---

## 119. Débito técnico

Débito técnico só pode ficar pendente quando:

- não bloqueia corretude;
- não bloqueia segurança;
- não viola arquitetura;
- não ameaça integridade;
- não é regressão criada pela fase;
- está explicitamente documentado.

---

# PARTE XXIII — ESTADO FINAL DESEJADO

## 120. Qualidade de portfólio

A versão final deve buscar:

- aplicação pública de demonstração;
- dados fictícios convincentes;
- fluxo de risco explicável;
- investigação auditável;
- arquitetura coerente;
- processamento idempotente;
- testes de concorrência;
- Security Gates;
- CI forte;
- observabilidade;
- documentação;
- pentest autorizado;
- screenshots;
- vídeo curto;
- release `v1.0.0`.

O número absoluto de testes não é meta.

---

## 121. Storytelling técnico

Ao final, o projeto deve ser explicável em uma frase:

> **A Central Antifraude recebe transações de pagamentos digitais, avalia risco de forma determinística e explicável, retorna uma decisão imediata e processa os efeitos operacionais de forma assíncrona, idempotente e auditável.**

---

## 122. Diferencial em relação ao Prisma RH

Não reproduzir o projeto anterior com outro domínio.

A Central Antifraude deve demonstrar principalmente:

- ingestão de eventos;
- idempotência;
- concorrência;
- tempo e ordenação;
- consistência;
- Outbox;
- consumidores idempotentes;
- processamento at-least-once;
- scoring;
- regras versionadas;
- backtesting;
- investigação;
- observabilidade.

O Prisma RH provou principalmente cálculo, histórico e auditoria de folha.

A Central Antifraude deve provar:

> **eventos + risco + decisão + investigação.**

---

# PARTE XXIV — PRINCÍPIOS FINAIS

## 123. Princípios inegociáveis

1. **Não inventar regras externas como fatos.**
2. **Não colocar IA no caminho crítico da decisão.**
3. **Não perder explicabilidade histórica.**
4. **Não confiar em exatamente-uma-entrega.**
5. **Não tratar idempotência apenas na aplicação.**
6. **Não ignorar concorrência.**
7. **Não permitir tenant vindo do payload como autoridade.**
8. **Não armazenar dados financeiros completos desnecessários.**
9. **Não usar tecnologia sem problema real.**
10. **Não fazer deploy ou push sem autorização.**
11. **Não parar após microetapas de uma fase já autorizada.**
12. **Não sacrificar corretude para encerrar uma fase.**

---

## 124. Pergunta obrigatória antes de qualquer decisão grande

> **Isso resolve um problema real da Central Antifraude ou só deixa o projeto mais impressionante?**

Se for apenas aparência arquitetural:

> **não implementar.**
