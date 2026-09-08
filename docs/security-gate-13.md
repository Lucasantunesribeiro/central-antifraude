# Security Gate 13 — dado de demonstração é dado que vaza

Data da execução: **2026-09-08**
Escopo: a fase que enche o banco de propósito.

Testes em `tests/CentralAntifraude.IntegrationTests/SeedNarrativoTests.cs`, mais
os doze gates anteriores, que continuam rodando.

Documentos: [`demonstracao.md`](demonstracao.md),
[ADR 0018](adr/0018-console-operacional-e-demonstracao.md).

---

## A premissa desta fase

Dado de demonstração tem uma propriedade perigosa: **ele é feito para ser
mostrado**. Vai para captura de tela, para vídeo, para o repositório público e
para a aba que fica aberta na apresentação. É o único dado do sistema cuja
finalidade é sair.

Por isso o risco aqui não é vazamento por falha — é vazamento por desenho. Um
seed com um nome de pessoa de verdade, um CPF plausível ou um número de cartão
que passa no algoritmo de Luhn atravessa todas as defesas do produto sem
disparar nenhuma, porque foi alguém que o escreveu ali.

---

## O que foi verificado

| Verificação | Resultado |
|---|---|
| Toda transação da demonstração usa referência `pi_demo_*` | ✅ teste |
| Nenhum cliente parece pessoa (sem nome próprio completo, sem `@`) | ✅ teste |
| Nenhum PAN, CVV ou dado de cartão em qualquer lugar do seed | ✅ nenhum campo existe no modelo |
| A senha da demonstração não está no código nem na documentação | ✅ vem de `Seed:SenhaPadrao` |
| O seed não roda sem essa configuração | ✅ falha fechada, herdado da Fase 1 |
| O seed não emite credencial de integração | ✅ decisão abaixo |
| `gitleaks detect` e `--no-git` | ✅ nada |
| Nenhuma rota nova nesta fase | ✅ matriz de autorização inalterada |

---

## As três decisões de segurança desta fase

### 1. A integração da demonstração nasce sem credencial

Emitir uma credencial no seed geraria um segredo que precisaria ir para algum
lugar — e o único lugar seguro seria fora do repositório, o que anularia a
conveniência que motivaria fazê-lo.

A integração é criada; quem quiser enviar transação de verdade emite a
credencial pela tela de Integrações, que é o caminho real e o que fica na
trilha de auditoria.

### 2. A conta pública recomendada é a de Auditor

Registrado como recomendação, e não como configuração aplicada — não há deploy
público ainda (ROADMAP 13.4).

O Auditor lê tudo e não age sobre nada. O Analista altera estado: assume casos,
registra notas, resolve investigações. Uma demonstração pública com perfil de
Analista exigiria estratégia de reset, e reset automático é uma rotina que apaga
dados — exatamente o tipo de coisa que não se acrescenta sem necessidade.

### 3. Os limiares continuam declarados como fictícios

`40` e `70` aparecem agora em documento de demonstração, que é onde uma
afirmação sem fonte tem mais chance de ser repetida como fato. Estão marcados
como configuração de demonstração em `demonstracao.md`, e não como padrão de
mercado (`CLAUDE.md` seção 112).

---

## Acessibilidade — medida, não estimada (ROADMAP 13.6)

Feita no navegador, sobre a página real:

| Item | Como foi verificado | Resultado |
|---|---|---|
| Rótulos | `element.labels` de todo `input`, `select` e `textarea` | 7 de 7 associados |
| Contraste | razão calculada sobre as cores computadas | 9 de 9 acima do mínimo |
| Semântica de tabela | `thead`, `scope` em todo cabeçalho, `caption` | completo |
| Estado sem depender de cor | forma no marcador: círculo, losango, triângulo | ✅ |
| Foco visível | `:focus-visible` com contorno de 2 px | ✅ |

**Dois defeitos reais de contraste foram encontrados e corrigidos**: rótulo de
filtro e cabeçalho de tabela ficavam em 4,15:1 contra o mínimo de 4,5:1 — e são
os menores textos da interface, com 9,5 px.

Uma suspeita levantada pela árvore de acessibilidade — filtros sem nome — **não
se confirmou**: a ferramenta mostrava o valor selecionado, e não o nome. A
medição direta no DOM mostrou os sete controles corretamente rotulados. Fica
registrado porque a conclusão apressada teria produzido uma mudança grande em 32
pontos do código, sem defeito para corrigir.

---

## O que este gate **não** cobre

- **Quatro telas não foram conferidas a olho**: regras, backtests, auditoria e
  administração. Elas herdam o mesmo sistema de estilos e passam nos testes,
  mas herança não é verificação.
- **Nenhuma largura de telefone foi verificada.** O console não é feito para
  telefone; existe apenas o mínimo para não quebrar numa janela estreita.
- **Nenhum leitor de tela real foi usado.** O que foi medido é a estrutura que
  um leitor de tela consome, não a experiência de quem usa um.
- **Sem teste automatizado de contraste.** A medição foi feita uma vez, à mão.
  Uma mudança de paleta futura não seria pega por nada — fica como débito
  nomeado, e não como cobertura.
