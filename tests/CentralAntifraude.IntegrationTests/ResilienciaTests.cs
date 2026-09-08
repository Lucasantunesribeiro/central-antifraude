using CentralAntifraude.Application.Mensageria;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// O que acontece quando uma dependencia some. (ROADMAP 12.6)
///
/// **O que esta classe NAO repete.** Mensagem envenenada indo para a DLQ,
/// entrega duplicada, worker caindo depois do efeito, despachantes concorrentes
/// e retry de serializacao ja tem teste desde as Fases 4 e 5 —
/// `BackboneAssincronoTests`, `FilaDeMensagensTests` e `ConcorrenciaTests`.
/// Repetir aqui daria a impressao de cobertura nova sem acrescentar risco
/// coberto.
///
/// O que falta e o outro lado: nao a mensagem ruim, e sim a **dependencia
/// ausente**. Banco fora, fila fora, efeito falhando, acumulo represado e
/// cliente que desiste no meio. Sao cinco situacoes em que o sistema nao
/// recebe erro nenhum de dado — ele simplesmente nao consegue continuar — e o
/// que se exige dele e sempre a mesma coisa: **parar de um jeito de que da
/// para voltar**.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class ResilienciaTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;

    public ResilienciaTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"res-{Guid.NewGuid():N}"[..14],
            Cancelamento);

        await CenarioDeMensageria.LimparAsync(_banco.StringDeConexao, Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();
        _integracao = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenant, Cancelamento);
    }

    public async ValueTask DisposeAsync()
    {
        _cliente?.Dispose();

        if (_fabrica is not null)
        {
            await _fabrica.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Banco indisponivel
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Com_o_banco_fora_o_processo_continua_vivo_e_so_a_prontidao_cai()
    {
        // Porta sem ninguem escutando: a conexao e recusada de imediato, sem
        // esperar timeout. Sem senha na string — o servidor nunca chega a
        // pedir, e uma credencial falsa no repositorio so serviria para
        // ensinar o varredor de segredos a ignorar linhas parecidas.
        var conexaoMorta =
            "Host=127.0.0.1;Port=59987;Database=inexistente;Username=ninguem;Timeout=2";

        await using var fabricaSemBanco = new FabricaDaApi(conexaoMorta);
        using var cliente = fabricaSemBanco.CriarClienteSemCookieAutomatico();

        // Vivo: o processo esta de pe e nao deve ser reiniciado por ninguem.
        using (var vivo = await cliente.GetAsync(new Uri("/health/live", UriKind.Relative), Cancelamento))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, vivo.StatusCode);
        }

        // Pronto: nao. **A separacao existe exatamente para este momento.** Um
        // health check unico faria um banco lento derrubar e reiniciar um
        // processo saudavel, e o reinicio nao consertaria o banco — so tiraria
        // do ar a unica instancia que ainda poderia responder quando ele
        // voltasse.
        using var pronto = await cliente.GetAsync(new Uri("/health/ready", UriKind.Relative), Cancelamento);

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, pronto.StatusCode);

        var corpo = await pronto.Content.ReadAsStringAsync(Cancelamento);

        // A resposta diz QUE nao esta pronto, e nao POR QUE. A mensagem de
        // excecao de um driver de banco carrega host, porta, base e usuario — e
        // o endpoint de saude e a superficie mais exposta que existe.
        Assert.Contains("Unhealthy", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("59987", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("ninguem", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inexistente", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", corpo, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Fila indisponivel
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Com_a_fila_fora_o_evento_fica_pendente_e_sai_quando_ela_volta()
    {
        using (var resposta = await IngerirAsync("res-fila"))
        {
            resposta.EnsureSuccessStatusCode();
        }

        var quebrada = new AlternanciaDeFalha();

        await using (var cenario = CenarioDeMensageria.Criar(
            _banco.StringDeConexao,
            envolverFila: fila => new FilaQueFalhaAoEnviar(fila, quebrada)))
        {
            var resultado = await cenario.Despachante.DespacharLoteAsync(Cancelamento);

            Assert.Equal(0, resultado.Publicados);
            Assert.Equal(1, resultado.Falhados);
        }

        await using (var leitura = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId))
        {
            var evento = Assert.Single(await leitura.EventosDeSaida.ToListAsync(Cancelamento));

            // Continua pendente, com a tentativa contada. **Marcar como
            // publicado seria o desastre silencioso**: o evento nunca saiu, e
            // ninguem jamais saberia — a alternativa correta e uma duplicata,
            // que o consumidor idempotente resolve (CLAUDE.md secao 40).
            Assert.Null(evento.PublicadoEm);
            Assert.Equal(1, evento.TentativasDePublicacao);
        }

        // A fila volta. Nada de intervencao manual, nada de reprocessamento
        // especial: o proximo ciclo do mesmo laco resolve.
        await using (var cenario = CenarioDeMensageria.Criar(_banco.StringDeConexao))
        {
            var resultado = await cenario.Despachante.DespacharLoteAsync(Cancelamento);

            Assert.Equal(1, resultado.Publicados);

            var mensagens = await cenario.Fila.ReceberAsync(
                IFilaDeMensagens.FilaOperacional,
                10,
                Cancelamento);

            Assert.Single(mensagens);
        }
    }

    // -----------------------------------------------------------------------
    // Efeito falhando no worker
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Um_efeito_que_falha_devolve_a_mensagem_e_nao_deixa_efeito_pela_metade()
    {
        using (var resposta = await IngerirAsync("res-efeito"))
        {
            resposta.EnsureSuccessStatusCode();
        }

        await using (var cenario = CenarioDeMensageria.Criar(
            _banco.StringDeConexao,
            manipuladores: [new EfeitoQueSempreFalha()]))
        {
            await cenario.Despachante.DespacharLoteAsync(Cancelamento);

            var consumo = await cenario.Processador.ConsumirLoteAsync(Cancelamento);

            Assert.Equal(1, consumo.Falhados);
            Assert.Equal(0, consumo.Processados);
        }

        await using var leitura = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        // A mensagem NAO foi apagada: a visibilidade expira e ela volta. Apagar
        // uma mensagem cujo efeito falhou trocaria uma repeticao por uma perda,
        // e perda nao tem conserto automatico.
        var naFila = await leitura.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM fila_de_mensagens")
            .SingleAsync(Cancelamento);

        Assert.Equal(1, naFila);

        // E a marca na Inbox tambem nao ficou. Se ela tivesse sido gravada, a
        // reentrega encontraria "ja processado" e o efeito NUNCA aconteceria —
        // o pior desfecho possivel, porque parece sucesso.
        Assert.Empty(await leitura.EventosProcessados
            .IgnoreQueryFilters()
            .Where(e => e.Consumidor == EfeitoQueSempreFalha.Nome)
            .ToListAsync(Cancelamento));
    }

    // -----------------------------------------------------------------------
    // Acumulo represado
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Um_acumulo_maior_que_o_lote_e_drenado_sem_perder_nem_repetir()
    {
        const int Total = 55;

        for (var i = 0; i < Total; i++)
        {
            using var resposta = await IngerirAsync($"res-acumulo-{i}");
            resposta.EnsureSuccessStatusCode();
        }

        await using var cenario = CenarioDeMensageria.Criar(_banco.StringDeConexao);

        // O lote e de 20. Um acumulo de 55 exige tres ciclos — e o teste existe
        // para provar que o despachante NAO tenta esvaziar tudo de uma vez: um
        // lote ilimitado seguraria uma transacao aberta por tempo indefinido e
        // travaria as linhas para todos os outros despachantes.
        var publicados = 0;
        var ciclos = 0;

        while (ciclos < 10)
        {
            ciclos++;
            var resultado = await cenario.Despachante.DespacharLoteAsync(Cancelamento);
            publicados += resultado.Publicados;

            if (resultado.Total == 0)
            {
                break;
            }

            Assert.True(resultado.Total <= DespachanteDeEventosLimite);
        }

        Assert.Equal(Total, publicados);

        await using var leitura = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        Assert.Empty(await leitura.EventosDeSaida
            .IgnoreQueryFilters()
            .Where(e => e.PublicadoEm == null)
            .ToListAsync(Cancelamento));

        var naFila = await leitura.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM fila_de_mensagens")
            .SingleAsync(Cancelamento);

        Assert.Equal(Total, naFila);
    }

    // -----------------------------------------------------------------------
    // Cliente que desiste
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Um_cliente_que_desiste_no_meio_nao_deixa_transacao_sem_avaliacao()
    {
        // Cada tentativa e cancelada em um instante diferente da operacao. O
        // teste nao tenta acertar o momento exato — ele afirma a INVARIANTE que
        // precisa valer em qualquer momento: transacao e avaliacao entram no
        // mesmo commit, entao nao existe transacao gravada sem decisao.
        var interrompidas = 0;

        for (var i = 0; i < 12; i++)
        {
            using var origem = new CancellationTokenSource(TimeSpan.FromMilliseconds(1 + (i * 2)));

            try
            {
                using var resposta = await EnviarAsync(
                    $"res-cancela-{i}",
                    $"cli-res-cancela-{i}",
                    origem.Token);
            }
            catch (Exception excecao) when (excecao is OperationCanceledException or HttpRequestException)
            {
                // Desistir e o cenario, e nao a falha.
                interrompidas++;
            }
        }

        // Sem isto o teste passaria vazio: doze requisicoes que completassem
        // normalmente satisfariam a invariante sem nunca exercitar o
        // cancelamento. A ingestao completa leva ordens de grandeza mais que os
        // milissegundos concedidos aqui, entao interromper e o caso comum — mas
        // "comum" nao e "verificado", e por isso a contagem e afirmada.
        Assert.True(interrompidas > 0, "Nenhuma requisicao chegou a ser interrompida.");

        await using var leitura = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var transacoes = await leitura.Transacoes
            .Select(t => t.Id)
            .ToListAsync(Cancelamento);

        var avaliadas = await leitura.AvaliacoesDeRisco
            .Select(a => a.TransacaoId)
            .ToListAsync(Cancelamento);

        // Meia gravacao aqui significaria uma transacao registrada que nunca
        // recebeu decisao — invisivel para o integrador, que recebeu erro de
        // rede, e invisivel para o analista, que so ve o que foi avaliado.
        Assert.All(transacoes, id => Assert.Contains(id, avaliadas));
    }

    // -----------------------------------------------------------------------
    // Auxiliares
    // -----------------------------------------------------------------------

    private const int DespachanteDeEventosLimite =
        CentralAntifraude.Infrastructure.Mensageria.DespachanteDeEventos.TamanhoDoLote;

    private Task<HttpResponseMessage> IngerirAsync(string identificador) =>
        EnviarAsync(identificador, $"cli-{identificador}", Cancelamento);

    private Task<HttpResponseMessage> EnviarAsync(
        string identificador,
        string cliente,
        CancellationToken cancellationToken)
    {
        var requisicao = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(CenarioDeIngestao.CaminhoDaIngestao, UriKind.Relative))
        {
            Content = System.Net.Http.Json.JsonContent.Create(CenarioDeIngestao.Corpo(
                identificadorExterno: identificador,
                clienteExternoId: cliente,
                ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1))),
        };

        requisicao.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{CenarioDeIngestao.EsquemaDaChave} {_integracao.Chave}");

        requisicao.Headers.TryAddWithoutValidation(
            CenarioDeIngestao.CabecalhoDeIdempotencia,
            $"idem-{Guid.NewGuid():N}");

        return _cliente.SendAsync(requisicao, cancellationToken);
    }

    /// <summary>Interruptor da falha simulada.</summary>
    private sealed class AlternanciaDeFalha
    {
        public bool Quebrada { get; set; } = true;
    }

    /// <summary>
    /// Uma fila que aceita tudo, menos enviar.
    ///
    /// Decorar em vez de derrubar o servico e deliberado: derrubar o PostgreSQL
    /// levaria junto a Outbox, e o teste passaria a medir duas falhas ao mesmo
    /// tempo. Aqui o banco esta perfeito e so o broker sumiu — que e o caso
    /// interessante, porque e nele que a Outbox prova para que serve.
    /// </summary>
    private sealed class FilaQueFalhaAoEnviar : IFilaDeMensagens
    {
        private readonly IFilaDeMensagens _real;
        private readonly AlternanciaDeFalha _estado;

        public FilaQueFalhaAoEnviar(IFilaDeMensagens real, AlternanciaDeFalha estado)
        {
            _real = real;
            _estado = estado;
        }

        public Task EnviarAsync(string fila, string corpo, CancellationToken cancellationToken) =>
            _estado.Quebrada
                ? Task.FromException(new InvalidOperationException("Broker indisponivel."))
                : _real.EnviarAsync(fila, corpo, cancellationToken);

        public Task<IReadOnlyList<MensagemRecebida>> ReceberAsync(
            string fila,
            int quantidade,
            CancellationToken cancellationToken) =>
            _real.ReceberAsync(fila, quantidade, cancellationToken);

        public Task ApagarAsync(string recibo, CancellationToken cancellationToken) =>
            _real.ApagarAsync(recibo, cancellationToken);

        public Task DevolverAsync(string recibo, CancellationToken cancellationToken) =>
            _real.DevolverAsync(recibo, cancellationToken);

        public Task<int> ContarPendentesAsync(string fila, CancellationToken cancellationToken) =>
            _real.ContarPendentesAsync(fila, cancellationToken);

        public Task<int> ContarMortasAsync(string fila, CancellationToken cancellationToken) =>
            _real.ContarMortasAsync(fila, cancellationToken);
    }

    /// <summary>Um efeito que nunca conclui.</summary>
    private sealed class EfeitoQueSempreFalha : IManipuladorDeEvento
    {
        public const string Nome = "efeito-que-sempre-falha";

        public string Consumidor => Nome;

        public Task AplicarAsync(EnvelopeDeEvento envelope, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Efeito indisponivel."));
    }
}
