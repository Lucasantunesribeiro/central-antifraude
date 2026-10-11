# Central Antifraude — plano para um portfólio de nível sênior, meta 95+/100

**Responsável pela execução futura:** Codex, em outro chat.
**Data deste plano:** 08/10/2026.
**Base:** auditoria de 07/10/2026; dependências consultadas novamente em 08/10/2026.
**Nota atribuída na auditoria:** 87/100.
**Meta:** alcançar pelo menos 95/100 em uma nova avaliação baseada em evidências.
**Estado deste documento:** plano pendente de implementação. Nenhuma tarefa abaixo foi executada ao escrever este relatório.

## 1. Como usar este relatório no próximo chat

Leia [AGENTS.md](AGENTS.md), [ROADMAP.md](ROADMAP.md) e as decisões relacionadas antes de implementar. Este relatório complementa a governança; não substitui as invariantes do produto.

O pedido que gerou este documento autorizou somente os relatórios. Quando Lucas pedir a execução deste plano, implemente as correções dentro desse escopo e continue até os critérios de conclusão. Explique cada grupo de mudanças em linguagem simples antes de editar código. Não peça confirmação para escolhas locais, reversíveis e já autorizadas.

Respeite as regras existentes para publicação, deploy, custos, segredos e ações destrutivas. Prepare mudanças e evidências locais antes de qualquer aprovação necessária para uma ação externa. Não trate a autorização para melhorar o projeto como autorização automática para alterar dados da demonstração publicada.

**Primeira ação:** compare o estado atual com o commit auditado e refaça somente as verificações que possam ter mudado. Não reaplique uma correção que já exista.

## 2. Base auditada e limites da avaliação

| Item | Evidência |
|---|---|
| Pasta | F:/Programacao_novos_projetos_2026/central_antifraude |
| Commit local auditado e ainda presente em 08/10 | 55f5f7d991a55963bc866d359648f9a03feaed7c |
| Estado inicial do Git | Sem alterações locais antes da criação deste relatório |
| Frontend, executado em 07/10 | 134 testes aprovados, lint aprovado e build aprovado |
| CI do mesmo commit | [Execução 34401717511](https://github.com/Lucasantunesribeiro/central-antifraude/actions/runs/34401717511), aprovada em setembro |
| Backend nessa CI | 552 testes unitários, 51 de arquitetura e 483 de integração aprovados |
| Demo observada em 07/10 | https://central-antifraude.vercel.app/ |
| npm audit, repetido em 08/10 | 2 altas: source-map-js e undici; zero achados com exclusão das dependências de desenvolvimento |
| Backend local | Não reexecutado: .NET 10 indisponível no PATH e Docker parado no ambiente da auditoria |

A consulta ao catálogo NuGet em 07/10 não encontrou correspondência de vulnerabilidade nas dependências dos arquivos de restauração locais existentes. Isso não substitui um restore e uma auditoria novos.

A revisão examinou caminhos críticos, testes, documentação e navegação da demo. Não foi uma revisão linha a linha de todo o sistema nem uma exploração ofensiva de produção. Nenhum defeito de alta gravidade no código da aplicação foi confirmado nessa amostra; isso não é uma certificação de ausência de falhas.

As versões publicadas não foram comprovadas como idênticas ao commit local. Preserve essa distinção na reavaliação.

## 3. Pontos fortes que devem continuar funcionando

- Decisões de risco determinísticas, com sinais, pesos e versões explicáveis.
- Idempotência por chave de integração e identificador externo, com verificação do conteúdo.
- Transação crítica com isolamento forte e repetição controlada da operação completa.
- Outbox, consumidores idempotentes e separação entre decisão imediata e efeitos posteriores.
- Investigação humana sem reescrever a decisão automática original.
- Isolamento entre organizações e testes com PostgreSQL real.
- Backtest usando o mesmo motor de risco e preservando o histórico.

Não adicionar microserviços, Redis, Kafka, IA no motor ou outra infraestrutura para subir a nota. O objetivo é comprovar e apresentar melhor as garantias existentes.

## 4. Prioridades e ordem

P0 = defeito crítico de integridade ou segurança da aplicação; P1 = necessário para a meta; P2 = acabamento necessário neste plano; opcional = não bloqueia a meta isoladamente.

| ID | Prioridade | Trabalho | Conclusão |
|---|---|---|---|
| CA-01 | P1 | Atualizar dependências afetadas e comprovar instalação reproduzível | [ ] |
| CA-02 | P1 | Resolver o envelhecimento do cenário demonstrativo | [ ] |
| CA-03 | P1 | Fazer a entrada e o roteiro mostrarem rapidamente o valor do produto | [ ] |
| CA-04 | P1 | Corrigir divergências de README, ROADMAP e guia da demo | [ ] |
| CA-05 | P1 | Renovar as evidências de testes e segurança | [ ] |
| CA-06 | P2 | Verificar acessibilidade, estados de erro e experiência móvel dos fluxos principais | [ ] |
| CA-07 | P2 | Entregar um conjunto curto de evidências técnicas e visuais | [ ] |

Ordem: preparar ambiente → CA-01 → CA-02 → CA-03 → CA-04 → CA-06 → CA-05 → CA-07 → reavaliar. Não há P0 confirmado para este projeto na auditoria.

## 5. Tarefas detalhadas

### CA-01 — Dependências seguras e instalação reproduzível

**Problema confirmado:** o npm audit devolveu duas vulnerabilidades altas, em source-map-js e undici. Elas pertencem à cadeia de ferramentas de desenvolvimento; não foram encontradas com o filtro de produção.

**Arquivos de partida:** [frontend/package.json](frontend/package.json), [frontend/package-lock.json](frontend/package-lock.json) e [.github/workflows/ci.yml](.github/workflows/ci.yml).

**Execução:**

1. Consultar novamente o audit e identificar o pacote pai de cada dependência afetada.
2. Preferir atualização compatível do pacote pai e do lockfile.
3. Usar override somente se a compatibilidade for demonstrada e a razão ficar registrada.
4. Fazer instalação limpa com npm ci em uma cópia de trabalho controlada.
5. Rodar testes, lint, verificação de formatação e build.
6. Manter o bloqueio da CI para severidade alta ou crítica. Não ocultar avisos para aprovar o pipeline.

**Aceite:**

- [ ] npm audit --audit-level=high retorna sucesso.
- [ ] npm audit --omit=dev também não apresenta alta ou crítica.
- [ ] Lockfile reproduz a instalação e npm ls não acusa dependências inválidas.
- [ ] Os checks do frontend passam após a atualização.
- [ ] Auditoria NuGet atual e varredura de segredos também estão registradas.

**Risco:** atualizar ferramentas pode alterar build, DOM de testes e suporte do Node. Não usar atualização forçada indiscriminada.

### CA-02 — Cenário demonstrativo que não perde valor com o tempo

**Problema observado:** a tela inicial do painel usa sete dias e mostrou zero transações. Ao selecionar 30 dias, apareceram 21 transações. Os alertas e casos existiam, mas a primeira impressão parecia pouco ativa.

**Causa no código:** [PaginaDoPainel.tsx](frontend/src/paginas/PaginaDoPainel.tsx), aproximadamente linha 33, inicia com sete dias. [SeedNarrativo.cs](src/CentralAntifraude.Infrastructure/Persistencia/SeedNarrativo.cs) usa a data da primeira execução e retorna sem renovar dados quando o marcador já existe.

**Decisão de implementação a registrar:** escolher o caminho mais simples que preserve o histórico:
- Um período demonstrativo identificado, com filtro inicial apropriado e navegação para a amostra; ou
- Renovação controlada do cenário em ambiente exclusivo de demonstração, com limites e execução explícita.

Mudar somente sete para 30 dias não resolve o envelhecimento permanente. Não alterar datas de transações históricas, decisões ou registros de auditoria para simular atividade recente. Não misturar dados da demo com dados de outras organizações.

**Aceite:**

- [ ] A entrada na demo mostra um cenário compreensível sem descobrir um filtro escondido.
- [ ] Se o cenário for histórico, o período fica claro e o visitante chega à amostra com uma ação simples.
- [ ] Uma verificação com relógio adiantado em 7, 30 e 90 dias comprova o comportamento escolhido.
- [ ] A renovação, se adotada, tem limite de execução, respeita isolamento e não duplica a mesma execução.
- [ ] Seeds e decisões continuam passando pelo motor real.
- [ ] Não existe script que apague dados publicados ou reescreva auditoria para cumprir o requisito.

### CA-03 — Entrada e roteiro com valor visível

**Problema observado:** a raiz começa com informações de saúde da infraestrutura; é preciso navegar até a entrada para entender o produto. Regras e backtests também ficam fora do alcance do perfil usado no acesso demonstrativo observado.

**Arquivos:** [PaginaInicial.tsx](frontend/src/paginas/PaginaInicial.tsx), [PaginaDeLogin.tsx](frontend/src/paginas/PaginaDeLogin.tsx), [docs/demonstracao.md](docs/demonstracao.md), [README.md](README.md) e o mecanismo de acesso demonstrativo em src/CentralAntifraude.Api/Identidade.

**Execução:**

1. Priorizar na entrada uma explicação curta do problema resolvido e o acesso demonstrativo.
2. Manter a consulta de saúde acessível para diagnóstico.
3. Oferecer um roteiro curto com links para painel, transação explicável e investigação existente.
4. Mostrar regras e backtest por vídeo, capturas ou fluxo local autorizado quando o visitante não tiver permissão.
5. Explicar de forma curta quais ações o perfil público permite. Conferir o comportamento real antes de descrevê-lo.

**Aceite:**

- [ ] Em até três minutos, um visitante entende entrada → sinais → decisão → investigação.
- [ ] A transação suspeita mostra os motivos da decisão.
- [ ] O falso positivo demonstra que conclusão humana e decisão automática são registros diferentes.
- [ ] Não são publicadas senhas ou credenciais privilegiadas.
- [ ] Políticas do backend e acesso entre organizações continuam intactos.
- [ ] O roteiro não pede que visitantes modifiquem dados compartilhados sem uma estratégia demonstrativa segura.

### CA-04 — Documentação factual e coerente

**Problemas confirmados:** README informa que falta a release v1.0.0 apesar de existir tag local. Há trechos de ROADMAP com conclusão posterior e tabelas anteriores ainda pendentes. As contagens de testes ficaram antigas. O guia de demo contém recomendações anteriores à publicação e um reset local destrutivo que precisa estar claramente delimitado.

**Arquivos:** [README.md](README.md), [ROADMAP.md](ROADMAP.md), [docs/demonstracao.md](docs/demonstracao.md), [docs/setup-local.md](docs/setup-local.md), [docs/pentest-v1.md](docs/pentest-v1.md) e [docs/custo-e-infraestrutura.md](docs/custo-e-infraestrutura.md).

**Execução:**

- Conferir separadamente tag local, tag remota e release no GitHub; não assumir que são a mesma coisa.
- Atualizar o status resumido do ROADMAP para não contradizer a conclusão detalhada.
- Conferir perfis de demo e permissões com código e testes.
- Datar contagens e resultados; vincular a uma execução em vez de criar vários totais manuais.
- Separar instruções de reset local das instruções de operação publicada.
- Manter no início do README: problema, demonstração, fluxo principal, diferenciais e execução.
- Tratar limitações do pentest como limitações, sem anunciar segurança absoluta.

**Aceite:** nenhum status atual se contradiz; links locais funcionam; comandos de setup foram verificados; o README permite entender e experimentar o produto em poucos minutos. Preserve a história técnica em documentos específicos quando resumir a entrada.

### CA-05 — Evidências atuais de correção, segurança e operação

**Tipo:** verificação necessária, não um novo defeito confirmado.

**Arquivos de partida:** [ConcorrenciaTests.cs](tests/CentralAntifraude.IntegrationTests/ConcorrenciaTests.cs), [MatrizDeAutorizacaoTests.cs](tests/CentralAntifraude.IntegrationTests/MatrizDeAutorizacaoTests.cs), [CargaTests.cs](tests/CentralAntifraude.IntegrationTests/CargaTests.cs), [SeedNarrativoTests.cs](tests/CentralAntifraude.IntegrationTests/SeedNarrativoTests.cs), [AcessoDeDemonstracaoTests.cs](tests/CentralAntifraude.IntegrationTests/AcessoDeDemonstracaoTests.cs), [.github/workflows/ci.yml](.github/workflows/ci.yml).

**Execução e aceite:**

- [ ] Reexecutar unitários, arquitetura e integração com PostgreSQL real no commit final.
- [ ] Preservar os testes de duplicidade, concorrência, isolamento, autorização e decisão histórica.
- [ ] Rodar novamente os testes de concorrência quando esse caminho for alterado.
- [ ] Documentar diferenças entre ingestão recebida, avaliação e conclusão dos efeitos assíncronos.
- [ ] Verificar um cenário local de falha e recuperação do processamento sem alerta/caso duplicado.
- [ ] Registrar latência de ingestão e consultas principais com volume, ambiente e condições.
- [ ] Separar inicialização fria de execução aquecida; não apresentar uma medição como garantia de produção.
- [ ] Registrar resultado da auditoria de dependências e varredura de segredos com saída redigida.
- [ ] Não adicionar testes que apenas repetem a implementação ou fabricar cobertura para aumentar números.

Usar primeiro os testes e ferramentas existentes. Uma CI histórica aprovada não vale como evidência do novo commit.

### CA-06 — Acessibilidade e estados da interface

**Tipo:** revisão dirigida de acabamento; não houve confirmação de falha específica aqui.

Verificar painel, alertas, transação e investigação em larguras de 390, 768 e 1280 pixels, e somente com teclado.

**Aceite:**

- [ ] Navegação e ações principais acessíveis por teclado, com foco visível.
- [ ] Decisões diferenciadas por texto e forma, além de cor.
- [ ] Carregamento, falta de dados e falha de API com mensagens úteis e tentativa novamente quando apropriada.
- [ ] Sem rolagem horizontal da página em telas móveis; tabelas podem rolar dentro de seu contêiner.
- [ ] Nenhum erro inesperado no console nos fluxos demonstrados.
- [ ] Se surgir um defeito funcional, criar teste que reproduza esse defeito; não criar testes artificiais para mudanças apenas editoriais.

### CA-07 — Evidências que sustentam a apresentação de sênior

Atualizar as capturas já existentes em [docs/screenshots](docs/screenshots) quando a interface ou o cenário mudar. Produzir um roteiro de apresentação de três a cinco minutos usando as histórias reais.

Criar, ao concluir a implementação, docs/evidencias-portfolio-95.md com: commit final, mudanças por ID, comandos e resultados, CI se publicada, auditorias datadas, cenários testados, capturas e limitações.

**Aceite:** a apresentação mostra uma decisão explicável e um falso positivo investigado; a evidência técnica explica por que a ingestão repetida não duplica a transação e como a versão das regras preserva o histórico. Vídeo existente pode ser reaproveitado se continuar fiel.

## 6. Ambiente e comandos de verificação

O ambiente da auditoria tinha SDKs .NET 9 no PATH e Node 22.17.0. O projeto pede SDK 10.0.400 em [global.json](global.json). Preparar o SDK compatível e Docker antes dos testes; não baixar o alvo do projeto para esconder a falta do ambiente.

Executar cada comando e verificar seu código de saída. Os blocos abaixo são uma lista, não um script que pode ignorar falhas anteriores.

**Na raiz:**

~~~powershell
git status --porcelain
git rev-parse HEAD
dotnet --list-sdks
docker info
dotnet restore CentralAntifraude.slnx
dotnet build CentralAntifraude.slnx --configuration Release --no-restore
dotnet test tests/CentralAntifraude.UnitTests/CentralAntifraude.UnitTests.csproj --configuration Release --no-build
dotnet test tests/CentralAntifraude.ArchitectureTests/CentralAntifraude.ArchitectureTests.csproj --configuration Release --no-build
dotnet test tests/CentralAntifraude.IntegrationTests/CentralAntifraude.IntegrationTests.csproj --configuration Release --no-build
dotnet list CentralAntifraude.slnx package --vulnerable --include-transitive --format json --output-version 1
~~~

O comando de auditoria NuGet pode encerrar com sucesso mesmo com achados: inspecionar o JSON e aplicar a regra da CI.

**Em frontend, durante a implementação autorizada:**

~~~powershell
node --version
npm ci
npm ls
npm run lint
npm run format:check
npm run test
npm run build
npm audit --audit-level=high
npm audit --omit=dev
~~~

Executar a varredura de segredos conforme a CI, com redação dos valores. Não copiar tokens ou saída sensível para o relatório.

## 7. Critério para reavaliar em 95+/100

A nota anterior foi julgamento técnico; a rubrica abaixo orienta a nova avaliação, sem fingir precisão científica ou garantir aumento automático.

| Dimensão | Peso |
|---|---:|
| Correção e integridade dos dados | 25 |
| Segurança | 20 |
| Testes e reprodução dos resultados | 20 |
| Clareza e manutenção da arquitetura | 15 |
| Demonstração, interface e desempenho | 10 |
| Documentação e evidências | 10 |
| Total | 100 |

Uma distribuição como 24 + 19 + 19 + 14 + 10 + 9 chega a 95. Quem reavaliar deve justificar cada pontuação pelo resultado real.

**Condições obrigatórias antes de atribuir 95 ou mais:**

- [ ] CA-01 a CA-07 concluídos ou substituídos por solução equivalente, com justificativa e prova.
- [ ] Nenhum defeito importante confirmado de segurança ou integridade ficou aberto.
- [ ] Instalação limpa e verificações completas aprovadas no commit entregue.
- [ ] Alertas altos/críticos conhecidos resolvidos, sem remover gates.
- [ ] Cenário demonstrativo mantém seu valor mesmo com a passagem do tempo.
- [ ] Demonstração publicada verificada após eventual implantação autorizada; preparo local sozinho não comprova o ambiente público.
- [ ] Documentação descreve o que realmente existe.
- [ ] Sem quebra das garantias de tenant, histórico, idempotência e risco determinístico.
- [ ] Relatório final com resultados, limitações e nova nota fundamentada.

## 8. Melhorias opcionais

Não são pré-requisito isolado para 95: novo tema visual, novos gráficos sem pergunta de negócio, mais provedores cloud, novos módulos de IA e testes adicionais apenas para aumentar contagem.

Performance só justifica refatoração adicional se a medição mostrar um problema. Não reorganizar todo o projeto para cumprir este documento.

## 9. Mensagem sugerida para iniciar a execução em outro chat

> Trabalhe no projeto Central Antifraude. Leia AUDITORIA_PORTFOLIO_95.md e a governança do repositório. Autorizo implementar os itens obrigatórios deste plano, preservando arquitetura, regras de negócio e controles de segurança. Explique as mudanças de forma simples antes de editar. Execute testes, auditorias e verificação da demo; registre evidências e pendências por ID. Não faça push, deploy, mudanças destrutivas em dados publicados ou crie custos sem a autorização aplicável. Ao terminar, reavalie a nota com justificativa; não declare 95+/100 se os critérios ainda estiverem pendentes.

