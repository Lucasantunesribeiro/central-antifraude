using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Integracoes;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Transacoes;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.Infrastructure.Persistencia;

/// <summary>
/// As histórias que a demonstração precisa contar. (ROADMAP 13.2 e 13.3)
///
/// **Por que isto não é "gerar 500 transações aleatórias".** Um banco cheio de
/// ruído mostra que o sistema aguenta volume e não explica nada. Quem abre a
/// demonstração precisa conseguir olhar um alerta e entender, sem ajuda, por
/// que ele existe — e ver pelo menos um caso em que o motor errou. Cada
/// transação daqui foi escrita para produzir um sinal específico.
///
/// **As decisões não são escritas à mão.** O seed monta a transação, monta o
/// contexto histórico e chama o <see cref="MotorDeRisco"/> de verdade, com a
/// versão de perfil publicada de verdade. Se alguém mudar um peso de regra, os
/// números desta demonstração mudam junto — e é assim que tem que ser. Um seed
/// que gravasse `score = 75` direto viraria mentira na primeira alteração do
/// catálogo, e mentira em demonstração é pior do que tela vazia.
///
/// **Determinismo** (13.3): não há sorteio em nada que apareça na tela.
/// Identificadores, clientes, valores, dispositivos e países são fixos; os
/// horários são deslocamentos fixos a partir do instante da execução, para que
/// a demonstração pareça recente sem que os dados mudem de conteúdo. Os
/// identificadores internos são UUIDv7, portanto variam — mas nenhum deles é
/// parte da narrativa.
///
/// **Idempotência** (13.3): a presença da primeira transação da história A é o
/// marcador. Rodar de novo não duplica nada.
///
/// Todos os dados são fictícios (CLAUDE.md seção 92): não há PII, não há
/// cartão, e o instrumento de pagamento é sempre uma referência `pi_demo_*`.
/// </summary>
internal static class SeedNarrativo
{
    /// <summary>
    /// A primeira transação da história A. Se ela existe, o seed já rodou.
    ///
    /// Um marcador de dado real é melhor do que uma tabela de controle: ele não
    /// pode divergir do que de fato existe no banco.
    /// </summary>
    private const string Marcador = "demo-a-1";

    private const string NomeDaIntegracao = "Loja Demonstração";

    /// <summary>
    /// Executa o seed. Devolve quantas transações criou — zero quando já
    /// existiam.
    /// </summary>
    public static async Task<int> ExecutarAsync(
        CentralAntifraudeDbContext contexto,
        Guid organizacaoId,
        IReadOnlyDictionary<PerfilDeUsuario, (Guid Id, string Nome)> usuarios,
        MotorDeRisco motor,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentNullException.ThrowIfNull(usuarios);
        ArgumentNullException.ThrowIfNull(motor);

        var jaRodou = await contexto.Transacoes
            .IgnoreQueryFilters([CentralAntifraudeDbContext.FiltroDeTenant])
            .AnyAsync(
                t => t.OrganizacaoId == organizacaoId && t.IdentificadorExterno == Marcador,
                cancellationToken);

        if (jaRodou)
        {
            return 0;
        }

        var versaoDoPerfil = await contexto.VersoesDePerfilDeRisco
            .IgnoreQueryFilters([CentralAntifraudeDbContext.FiltroDeTenant])
            .Include(v => v.VersoesDeRegra)
            .Where(v => v.OrganizacaoId == organizacaoId)
            .OrderByDescending(v => v.PublicadaEm)
            .ThenByDescending(v => v.Numero)
            .FirstOrDefaultAsync(cancellationToken);

        // Sem perfil publicado não há como avaliar, e inventar decisão seria
        // exatamente o que esta classe existe para não fazer.
        if (versaoDoPerfil is null)
        {
            return 0;
        }

        var integracao = await GarantirIntegracaoAsync(contexto, organizacaoId, agora, cancellationToken);

        var oficina = new Oficina(contexto, organizacaoId, integracao.Id, versaoDoPerfil, motor, agora);

        // ---------------------------------------------------------------
        // História A — o comportamento normal, que é a maioria absoluta.
        //
        // Sem ela a demonstração mentiria por omissão: um painel em que toda
        // transação vira alerta descreve um motor quebrado, não um motor bom.
        // ---------------------------------------------------------------
        for (var i = 1; i <= 6; i++)
        {
            oficina.Registrar(
                identificador: $"demo-a-{i}",
                cliente: "cli-ana-recorrente",
                valor: 118.90m + i,
                minutosAtras: 60 * 24 * (7 - i) + 30,
                dispositivo: "disp-ana-notebook",
                pais: "BR");
        }

        // ---------------------------------------------------------------
        // História B — valor muito acima do histórico, de aparelho novo e de
        // outro país. Os três sinais somam e a decisão vira Bloquear.
        //
        // O histórico curto e barato vem antes de propósito: "acima do
        // histórico" só significa alguma coisa contra um passado.
        // ---------------------------------------------------------------
        for (var i = 1; i <= 3; i++)
        {
            oficina.Registrar(
                identificador: $"demo-b-hist-{i}",
                cliente: "cli-bruno-viajante",
                valor: 100m,
                minutosAtras: 60 * 24 * (4 - i),
                dispositivo: "disp-bruno-celular",
                pais: "BR");
        }

        var bloqueada = oficina.Registrar(
            identificador: "demo-b-compra-suspeita",
            cliente: "cli-bruno-viajante",
            valor: 920m,
            minutosAtras: 95,
            dispositivo: "disp-desconhecido-01",
            pais: "PT");

        // ---------------------------------------------------------------
        // História C — velocidade: várias tentativas em poucos minutos.
        //
        // Em ordem cronológica, e isso não é detalhe: o contexto de cada
        // transação são as ANTERIORES a ela. Enviadas ao contrário, a última a
        // chegar seria a mais antiga e a regra ficaria calada.
        // ---------------------------------------------------------------
        for (var i = 1; i <= 4; i++)
        {
            oficina.Registrar(
                identificador: $"demo-c-{i}",
                cliente: "cli-carla-apressada",
                valor: 74.50m,
                minutosAtras: 40 - (i * 3),
                dispositivo: "disp-carla-tablet",
                pais: "BR");
        }

        // A quinta fecha a rajada e é bem maior que as outras. Sem ela a
        // história terminaria em Permitir: velocidade sozinha vale +35 e o
        // limiar de revisão é 40. Não é defeito — é o mesmo ponto da história
        // D visto por outra regra —, mas a demonstração precisa de ao menos um
        // alerta ESPERANDO na fila, e este é ele: ninguém o assumiu ainda.
        oficina.Registrar(
            identificador: "demo-c-maior-da-rajada",
            cliente: "cli-carla-apressada",
            valor: 390m,
            // 26 minutos, e nao 24: a janela de velocidade tem dez minutos, e a
            // 24 a primeira da rajada ja tinha saido dela — sobravam duas
            // anteriores e a regra ficava calada.
            minutosAtras: 26,
            dispositivo: "disp-carla-tablet",
            pais: "BR");

        // ---------------------------------------------------------------
        // História D — divergência geográfica isolada, sem valor atípico.
        //
        // **Esta história termina em Permitir, e é de propósito.** Um sinal de
        // +25 não alcança o limiar de revisão (40), então a transação passa
        // com o sinal registrado. É a demonstração de que o score é aditivo:
        // uma evidência sozinha levanta a sobrancelha e não segura ninguém.
        //
        // Sem um caso assim, quem vê o produto conclui que todo sinal vira
        // alerta — e passaria a estranhar o dia em que um não virar.
        // ---------------------------------------------------------------
        for (var i = 1; i <= 3; i++)
        {
            oficina.Registrar(
                identificador: $"demo-d-hist-{i}",
                cliente: "cli-diego-fiel",
                valor: 260m,
                minutosAtras: 60 * 24 * (5 - i) + 15,
                dispositivo: "disp-diego-desktop",
                pais: "BR");
        }

        oficina.Registrar(
            identificador: "demo-d-de-outro-pais",
            cliente: "cli-diego-fiel",
            valor: 265m,
            minutosAtras: 150,
            dispositivo: "disp-diego-desktop",
            pais: "AR");

        // ---------------------------------------------------------------
        // História E — o falso positivo (CLAUDE.md seção 94).
        //
        // Compras seguidas de um cliente que acabou de trocar de aparelho:
        // velocidade e dispositivo novo somam e o motor manda revisar. A
        // investigação humana conclui que era legítima.
        // ---------------------------------------------------------------
        for (var i = 1; i <= 3; i++)
        {
            oficina.Registrar(
                identificador: $"demo-e-hist-{i}",
                cliente: "cli-elis-madrugada",
                valor: 150m + (i * 10),
                minutosAtras: 60 * 24 * (4 - i) + 45,
                dispositivo: "disp-elis-antigo",
                pais: "BR");
        }

        // As três precisam caber na janela de dez minutos que antecede a
        // última: 27, 24 e 21 minutos atrás, contra 18 da final. Espaçadas em
        // quatro minutos, como estavam antes, a mais antiga caía fora da
        // janela, só duas contavam e a regra ficava calada — a história
        // existia no comentário e não no banco.
        for (var i = 1; i <= 3; i++)
        {
            oficina.Registrar(
                identificador: $"demo-e-{i}",
                cliente: "cli-elis-madrugada",
                valor: 199m + i,
                minutosAtras: 30 - (i * 3),
                dispositivo: "disp-elis-antigo",
                pais: "BR");
        }

        var falsoPositivo = oficina.Registrar(
            identificador: "demo-e-aparelho-novo",
            cliente: "cli-elis-madrugada",
            valor: 229m,
            minutosAtras: 18,
            dispositivo: "disp-elis-celular-novo",
            pais: "BR");

        // ---------------------------------------------------------------
        // História F — o segundo caso do mesmo padrão, em outro cliente.
        //
        // É ele que dá ao caso em aberto mais de um alerta: agrupar duas
        // ocorrências parecidas é justamente o que distingue um CASO de um
        // alerta solto (CLAUDE.md seção 65).
        // ---------------------------------------------------------------
        for (var i = 1; i <= 3; i++)
        {
            oficina.Registrar(
                identificador: $"demo-f-hist-{i}",
                cliente: "cli-fabio-parceiro",
                valor: 130m,
                minutosAtras: 60 * 24 * (4 - i) + 200,
                dispositivo: "disp-fabio-celular",
                pais: "BR");
        }

        var segundoBloqueio = oficina.Registrar(
            identificador: "demo-f-mesmo-padrao",
            cliente: "cli-fabio-parceiro",
            valor: 1180m,
            minutosAtras: 75,
            dispositivo: "disp-desconhecido-02",
            pais: "PT");

        await oficina.SalvarAsync(cancellationToken);

        await MontarInvestigacoesAsync(
            contexto,
            organizacaoId,
            usuarios,
            oficina,
            falsoPositivo,
            bloqueada,
            segundoBloqueio,
            agora,
            cancellationToken);

        return oficina.Total;
    }

    /// <summary>
    /// As duas investigações da narrativa.
    ///
    /// **Caso 1 — o falso positivo, fechado.** Aberto, assumido, com nota e
    /// resolvido como legítima. É a história que explica por que existe gente
    /// no processo: a decisão automática continua sendo `Revisar`, e o
    /// resultado humano não a reescreve.
    ///
    /// **Caso 2 — dois alertas, aberto.** Fica em análise de propósito: a
    /// demonstração precisa de uma fila com trabalho por fazer, e é ele que
    /// mostra o agrupamento e a linha do tempo em andamento (13.2, história F).
    /// </summary>
    private static async Task MontarInvestigacoesAsync(
        CentralAntifraudeDbContext contexto,
        Guid organizacaoId,
        IReadOnlyDictionary<PerfilDeUsuario, (Guid Id, string Nome)> usuarios,
        Oficina oficina,
        Transacao falsoPositivo,
        Transacao bloqueada,
        Transacao segundoBloqueio,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        if (!usuarios.TryGetValue(PerfilDeUsuario.AnalistaDeFraude, out var analista))
        {
            return;
        }

        var alertaDoFalsoPositivo = oficina.AlertaDe(falsoPositivo.Id);
        var alertaBloqueado = oficina.AlertaDe(bloqueada.Id);
        var alertaDoSegundo = oficina.AlertaDe(segundoBloqueio.Id);

        if (alertaDoFalsoPositivo is not null)
        {
            var caso = Caso.Abrir(
                organizacaoId,
                "Compras seguidas de um aparelho recém-trocado",
                [alertaDoFalsoPositivo],
                analista.Id,
                analista.Nome,
                agora.AddMinutes(-15));

            caso.Assumir(analista.Id, analista.Nome, agora.AddMinutes(-14));

            caso.AdicionarNota(
                "Cliente confirmou por telefone: trocou de celular ontem e estava " +
                "finalizando as compras da viagem. Cartão na posse dela, sem contestação.",
                analista.Id,
                analista.Nome,
                agora.AddMinutes(-11));

            var veredictos = caso.Resolver(
                ResultadoDaInvestigacao.Legitima,
                [alertaDoFalsoPositivo],
                analista.Id,
                analista.Nome,
                agora.AddMinutes(-10));

            Gravar(contexto, caso);
            contexto.ResultadosDeInvestigacao.AddRange(veredictos);
        }

        if (alertaBloqueado is not null)
        {
            List<Alerta> alertas = alertaDoSegundo is null
                ? [alertaBloqueado]
                : [alertaBloqueado, alertaDoSegundo];

            var caso = Caso.Abrir(
                organizacaoId,
                "Compra alta de outro país, aparelho desconhecido",
                alertas,
                analista.Id,
                analista.Nome,
                agora.AddMinutes(-70));

            caso.Assumir(analista.Id, analista.Nome, agora.AddMinutes(-68));

            caso.AdicionarNota(
                "Duas ocorrências do mesmo padrão em clientes diferentes na mesma tarde. " +
                "Aguardando retorno do time de risco do parceiro antes de concluir.",
                analista.Id,
                analista.Nome,
                agora.AddMinutes(-40));

            Gravar(contexto, caso);
        }

        await contexto.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Grava o caso com a linha do tempo junto.
    ///
    /// Eventos e notas NÃO são navegações do agregado — o caso os acumula em
    /// listas próprias e quem persiste precisa pedi-las. Sem isto o caso
    /// aparece na lista com o histórico vazio, e a tela de investigação, que é
    /// o coração da demonstração, abre sem nada para mostrar.
    ///
    /// É a mesma coisa que `RepositorioDeCasos.Adicionar` faz. O seed não usa
    /// o repositório porque roda sem identidade e o filtro de tenant o
    /// esvaziaria, mas o comportamento tem que ser o mesmo.
    /// </summary>
    private static void Gravar(CentralAntifraudeDbContext contexto, Caso caso)
    {
        contexto.Casos.Add(caso);
        contexto.EventosDoCaso.AddRange(caso.NovosEventos);
        contexto.NotasDoCaso.AddRange(caso.NovasNotas);
    }

    private static async Task<Integracao> GarantirIntegracaoAsync(
        CentralAntifraudeDbContext contexto,
        Guid organizacaoId,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        var existente = await contexto.Integracoes
            .IgnoreQueryFilters([CentralAntifraudeDbContext.FiltroDeTenant])
            .FirstOrDefaultAsync(
                i => i.OrganizacaoId == organizacaoId && i.Nome == NomeDaIntegracao,
                cancellationToken);

        if (existente is not null)
        {
            return existente;
        }

        // Integração sem credencial, de propósito: emitir uma credencial
        // geraria um segredo que teria de ir para algum lugar, e o único lugar
        // seguro seria fora daqui. Quem quiser enviar transação de verdade
        // emite a credencial pela tela de Integrações, que é o caminho real.
        var integracao = Integracao.Criar(organizacaoId, NomeDaIntegracao, agora);

        contexto.Integracoes.Add(integracao);
        await contexto.SaveChangesAsync(cancellationToken);

        return integracao;
    }

    /// <summary>
    /// Monta transação, avaliação e alerta como o caminho real faria.
    ///
    /// O contexto histórico é acumulado em memória a partir do que o próprio
    /// seed já registrou para aquele cliente, na mesma ordem e com os mesmos
    /// limites do provedor de produção — mais recente primeiro.
    /// </summary>
    private sealed class Oficina
    {
        private readonly CentralAntifraudeDbContext _contexto;
        private readonly Guid _organizacaoId;
        private readonly Guid _integracaoId;
        private readonly VersaoDePerfilDeRisco _versaoDoPerfil;
        private readonly MotorDeRisco _motor;
        private readonly DateTimeOffset _agora;

        private readonly Dictionary<string, List<TransacaoDoHistorico>> _historico =
            new(StringComparer.Ordinal);

        private readonly Dictionary<Guid, Alerta> _alertas = [];

        public Oficina(
            CentralAntifraudeDbContext contexto,
            Guid organizacaoId,
            Guid integracaoId,
            VersaoDePerfilDeRisco versaoDoPerfil,
            MotorDeRisco motor,
            DateTimeOffset agora)
        {
            _contexto = contexto;
            _organizacaoId = organizacaoId;
            _integracaoId = integracaoId;
            _versaoDoPerfil = versaoDoPerfil;
            _motor = motor;
            _agora = agora;
        }

        public int Total { get; private set; }

        public Alerta? AlertaDe(Guid transacaoId) =>
            _alertas.TryGetValue(transacaoId, out var alerta) ? alerta : null;

        public Transacao Registrar(
            string identificador,
            string cliente,
            decimal valor,
            int minutosAtras,
            string dispositivo,
            string pais)
        {
            var ocorridaEm = _agora.AddMinutes(-minutosAtras);

            // Recebida um pouco depois de ocorrida: os dois horários são
            // diferentes por natureza (CLAUDE.md seção 15), e uma demonstração
            // em que coincidem esconde justamente essa distinção.
            var recebidaEm = ocorridaEm.AddSeconds(9);

            var transacao = Transacao.Registrar(
                _organizacaoId,
                _integracaoId,
                identificador,
                Dinheiro.De(valor, "BRL"),
                ocorridaEm,
                recebidaEm,
                cliente,
                $"pi_demo_{identificador}",
                dispositivo,
                fingerprintDoIp: null,
                pais,
                chaveDeIdempotencia: $"seed-{identificador}",
                fingerprintDoPayload: $"seed-{identificador}",
                idDeCorrelacao: $"seed-demo-{identificador}");

            var contexto = new ContextoDeRisco(HistoricoDe(cliente, ocorridaEm));
            var avaliacao = _motor.Avaliar(transacao, _versaoDoPerfil, contexto, recebidaEm);

            _contexto.Transacoes.Add(transacao);
            _contexto.AvaliacoesDeRisco.Add(avaliacao);

            // O alerta nasce da mesma política que o worker aplica. O evento é
            // montado só para alimentá-la — ele não vai para a Outbox, porque
            // o efeito já está sendo criado aqui e publicá-lo faria o worker
            // tentar criar o alerta uma segunda vez.
            var alerta = PoliticaDeAlertas.Avaliar(
                _organizacaoId,
                TransacaoAvaliadaV1.De(transacao, avaliacao),
                Identificador.Novo(),
                transacao.IdDeCorrelacao ?? string.Empty,
                recebidaEm);

            if (alerta is not null)
            {
                _contexto.Alertas.Add(alerta);
                _alertas[transacao.Id] = alerta;
            }

            Lembrar(cliente, transacao);
            Total++;

            return transacao;
        }

        public Task<int> SalvarAsync(CancellationToken cancellationToken) =>
            _contexto.SaveChangesAsync(cancellationToken);

        /// <summary>Do mais recente para o mais antigo, e só o que veio antes.</summary>
        private List<TransacaoDoHistorico> HistoricoDe(string cliente, DateTimeOffset ate) =>
            _historico.TryGetValue(cliente, out var lista)
                ? [.. lista.Where(t => t.OcorridaEm <= ate).OrderByDescending(t => t.OcorridaEm)]
                : [];

        private void Lembrar(string cliente, Transacao transacao)
        {
            if (!_historico.TryGetValue(cliente, out var lista))
            {
                lista = [];
                _historico[cliente] = lista;
            }

            lista.Add(new TransacaoDoHistorico(
                transacao.OcorridaEm,
                transacao.Valor.Valor,
                transacao.Valor.Moeda,
                transacao.FingerprintDoDispositivo,
                transacao.PaisDeOrigem));
        }
    }
}
