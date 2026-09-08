# Screenshots — Central Antifraude v1

Capturas do ambiente publicado (`https://central-antifraude.vercel.app`), como
Analista de Fraude, em 2026-09-08.

| Arquivo | Tela | O que mostra |
|---|---|---|
| `01-painel-operacional.jpg` | Painel | Uma ação principal ("Trabalhar a fila"), volumes do período e a distribuição de decisões |
| `02-fila-de-alertas.jpg` | Alertas | Fila filtrável; cada linha traz prioridade, decisão, score e os sinais que a justificam |
| `03-transacao-sinais-explicaveis.jpg` | Transação | **"Por que esta transação recebeu 75"** — score, versão do perfil e do motor, e cada sinal com a matemática e a versão da regra |
| `04-casos.jpg` | Casos | Investigações com situação, responsável, maior score e resultado |
| `05-caso-falso-positivo.jpg` | Detalhe do caso | Um **falso positivo**: `Revisar` do motor, `Legítima` do humano, com timeline append-only |
| `06-regras-versionadas.jpg` | Regras | Perfil de risco versionado, limiares como configuração de demonstração, catálogo tipado |

As telas administrativas (CRUD de usuários, integrações) foram deixadas de fora
de propósito — a seção 15.8 do ROADMAP pede as telas que vendem o produto, não o
administrativo.
