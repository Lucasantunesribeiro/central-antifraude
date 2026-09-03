# ADR 0002 — Identificadores internos são UUID versão 7

- **Status:** aceito
- **Data:** 2026-09-03
- **Fase:** 0 — Fundação Técnica

O `ROADMAP.md` (seção 0.5) exige que esta escolha seja **congelada na Fase 0**.
Ela é. Mudar depois significaria migrar toda chave primária do sistema.

## Contexto

Os identificadores da Central Antifraude aparecem em três lugares com
exigências diferentes:

1. **chave primária no PostgreSQL** — milhões de transações, com consultas por
   janela de tempo em quase toda regra de risco;
2. **identificador público na API** — visível ao integrador e ao analista;
3. **correlação entre serviços** — transação, avaliação, evento de outbox,
   mensagem e alerta precisam ser ligáveis.

## Alternativas consideradas

### `bigint` sequencial

Ótimo para o índice, mas vaza contagem interna. Um integrador que recebe
`transacao/8123` e `transacao/8140` sabe quantas transações a plataforma
processou entre as duas — informação de negócio de outros tenants, entregue
de graça. Reprovado pela seção 102 do `CLAUDE.md`.

### UUID versão 4

Não vaza nada, mas é aleatório. Chaves geradas em sequência caem em páginas
espalhadas do índice B-tree, o que aumenta a fragmentação e o número de
páginas sujas a cada escrita. Num sistema cuja carga é inserção contínua de
transações, isso trabalha contra o caso de uso principal.

### ULID

Resolve a ordenação, mas não é tipo nativo do PostgreSQL nem do .NET.
Precisaria de conversor, de coluna `text` ou `bytea`, e de uma biblioteca de
terceiro no Domain — que este projeto mantém sem dependências.

## Decisão

**UUID versão 7 (RFC 9562)**, gerado pela aplicação com
`Guid.CreateVersion7()`, persistido na coluna nativa `uuid` do PostgreSQL.

O UUIDv7 tem 48 bits de timestamp Unix em milissegundos nos bytes iniciais.
Como o `uuid` do PostgreSQL compara byte a byte, identificadores gerados em
instantes crescentes ficam próximos no índice — a vantagem do sequencial, sem
expor contagem.

Ponto único de geração: `Identificador.Novo()`, em
`src/CentralAntifraude.Domain/Primitivos/Identificador.cs`.

`Guid.NewGuid()` está **proibido em `src/`** por `src/BannedSymbols.txt`, que
quebra a compilação. Sem isso, a decisão duraria até o primeiro desenvolvedor
distraído.

## Consequências

- O identificador revela o **instante aproximado de criação**. Para transações
  e alertas isso não é problema: o horário já é um campo do recurso.
- Se algum identificador futuro não puder revelar o instante de criação (um
  token, por exemplo), ele **não** é um identificador de entidade e deve usar
  outro mecanismo. Este ADR não cobre esse caso.
- Não há dependência de biblioteca externa: `Guid.CreateVersion7()` é da
  biblioteca padrão desde o .NET 9.

## Verificação

`tests/CentralAntifraude.UnitTests/Dominio/IdentificadorTests.cs`.

O teste de ordenação compara os bytes em big-endian, **do jeito que o
PostgreSQL compara** — e não com `Guid.CompareTo` do .NET, que ordena campo a
campo com sinal e daria uma resposta diferente. Um teste que usasse
`CompareTo` passaria sem provar a propriedade que motiva a decisão.
