using System.Net.Http.Json;
using System.Text.Json;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// O console operacional, ponta a ponta e contra o PostgreSQL real.
///
/// **O que esta fase precisa provar sao tres coisas.**
///
/// A primeira e que filtrar, ordenar e paginar respondem juntos: o total
/// precisa refletir o filtro, senao a paginacao mostra paginas vazias no fim e
/// ninguem confia mais na contagem.
///
/// A segunda e que o detalhe da transacao reune o que estava espalhado —
/// avaliacao, alerta, caso, veredito humano e o numero de protocolo que o
/// suporte usa.
///
/// A terceira e que o painel conta o que diz que conta. "Recebidas" e
/// "avaliadas" sao numeros diferentes por natureza, e igualar os dois
/// esconderia exatamente o comportamento — evento atrasado — que o produto
/// existe para tratar.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class ConsoleOperacionalTests : IAsyncLifetime
{
    private const string Transacoes = "/api/transacoes";
    private const string Painel = "/api/painel";

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenant = null!;
    private IntegracaoDeTeste _integracao = null!;
    private CenarioDeMensageria _mensageria = null!;

    private readonly Dictionary<PerfilDeUsuario, string> _tokens = [];

    public ConsoleOperacionalTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        _tenant = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"cons-{Guid.NewGuid():N}"[..14],
            Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();
        _integracao = await CenarioDeIngestao.CriarIntegracaoAsync(_cliente, _tenant, Cancelamento);

        await CenarioDeMensageria.LimparAsync(_banco.StringDeConexao, Cancelamento);

        _mensageria = CenarioDeMensageria.Criar(_banco.StringDeConexao);
    }

    public async ValueTask DisposeAsync()
    {
        if (_mensageria is not null)
        {
            await _mensageria.DisposeAsync();
        }

        _cliente?.Dispose();

        if (_fabrica is not null)
        {
            await _fabrica.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Filtros
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_filtro_de_decisao_muda_a_lista_e_o_total_junto()
    {
        // Total e lista precisam falar do MESMO conjunto. Contar antes de
        // filtrar daria paginas vazias no fim, e ninguem reporta isso — so
        // deixa de confiar na contagem.
        await IngerirAsync("dec", quantidade: 3, cliente: "cli-normal");
        await IngerirBloqueioAsync("dec-bloqueio");

        var todas = await ListarAsync(string.Empty);
        var permitidas = await ListarAsync("decisao=Permitir");
        var revisar = await ListarAsync("decisao=Revisar");
        var bloqueadas = await ListarAsync("decisao=Bloquear");

        // As tres partes somam o todo: se o total nao acompanhasse o filtro,
        // esta conta nao fecharia.
        Assert.Equal(
            Total(todas),
            Total(permitidas) + Total(revisar) + Total(bloqueadas));

        Assert.True(Total(bloqueadas) > 0);

        Assert.All(
            bloqueadas.GetProperty("itens").EnumerateArray(),
            item => Assert.Equal("Bloquear", item.GetProperty("decisao").GetString()));
    }

    [Fact]
    public async Task O_filtro_de_score_respeita_as_duas_bordas()
    {
        await IngerirAsync("score", quantidade: 2, cliente: "cli-baixo");
        await IngerirBloqueioAsync("score-alto");

        var altas = await ListarAsync("scoreMinimo=70");
        var baixas = await ListarAsync("scoreMaximo=10");

        Assert.True(Total(altas) > 0);
        Assert.True(Total(baixas) > 0);

        // O que importa e a SEMANTICA da borda, e nao um numero decorado do
        // cenario: um teste que afirma "2" quebra quando o cenario ganha uma
        // transacao e nao diz nada sobre o filtro.
        Assert.All(
            altas.GetProperty("itens").EnumerateArray(),
            item => Assert.True(item.GetProperty("score").GetInt32() >= 70));

        Assert.All(
            baixas.GetProperty("itens").EnumerateArray(),
            item => Assert.True(item.GetProperty("score").GetInt32() <= 10));

        // E os dois conjuntos nao se tocam.
        Assert.Empty(Ids(altas).Intersect(Ids(baixas)));
    }

    [Fact]
    public async Task O_filtro_de_sinal_encontra_quem_a_regra_pegou()
    {
        // Responde "quais transacoes a regra de velocidade pegou?" sem
        // precisar de um backtest.
        await IngerirRajadaAsync("veloc");

        var comVelocidade = await ListarAsync(
            $"tipoDeRegra={nameof(TipoDeRegra.VelocidadePorCliente)}");

        Assert.True(comVelocidade.GetProperty("total").GetInt32() > 0);

        // Uma transacao com tres sinais do mesmo tipo nao pode aparecer tres
        // vezes: a consulta usa subconsulta, e nao juncao.
        var ids = comVelocidade
            .GetProperty("itens")
            .EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task A_busca_encontra_por_identificador_e_por_cliente()
    {
        await IngerirAsync("achavel", quantidade: 1, cliente: "cli-procurado-123");
        await IngerirAsync("outro", quantidade: 1, cliente: "cli-qualquer");

        var porIdentificador = await ListarAsync("busca=achavel");
        var porCliente = await ListarAsync("busca=procurado");

        Assert.Equal(1, porIdentificador.GetProperty("total").GetInt32());
        Assert.Equal(1, porCliente.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Curinga_digitado_na_busca_e_texto_e_nao_operador()
    {
        // **Sem o escape, este `%` devolveria a tabela inteira** e a tela
        // pareceria filtrada — pior do que um erro, porque uma lista cheia nao
        // levanta suspeita.
        await IngerirAsync("literal", quantidade: 2, cliente: "cli-literal");

        // `%25` e o `%` codificado. Como curinga, `%l` casaria com tudo que
        // contem "l" — inclusive as duas transacoes acima.
        var comCuringa = await ListarAsync("busca=%25l");

        Assert.Equal(0, Total(comCuringa));

        // A mesma busca sem o curinga encontra as duas: o que muda e so o `%`.
        Assert.Equal(2, Total(await ListarAsync("busca=literal")));
    }

    [Fact]
    public async Task O_periodo_filtra_por_ocorrencia_e_nao_por_chegada()
    {
        // Quem investiga procura "o que aconteceu na terca". Uma transacao
        // atrasada aconteceu na terca mesmo tendo chegado na quinta, e filtrar
        // pela chegada a esconderia de quem foi procura-la.
        var antiga = DateTimeOffset.UtcNow.AddDays(-20);

        using var resposta = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracao.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(
                identificadorExterno: $"atrasada-{Guid.NewGuid():N}"[..20],
                clienteExternoId: "cli-atrasado",
                ocorridaEm: antiga),
            Cancelamento);

        resposta.EnsureSuccessStatusCode();

        await IngerirAsync("recente", quantidade: 1, cliente: "cli-recente");

        var ultimosDias = await ListarAsync(
            $"de={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-2).ToString("O"))}");

        Assert.Equal(1, ultimosDias.GetProperty("total").GetInt32());

        var tudo = await ListarAsync(
            $"de={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-30).ToString("O"))}");

        Assert.Equal(2, tudo.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Ordenar_por_score_coloca_o_mais_alto_no_topo()
    {
        await IngerirAsync("ord", quantidade: 2, cliente: "cli-ord");
        await IngerirBloqueioAsync("ord-alto");

        var lista = await ListarAsync("ordenarPor=score&direcao=desc");

        var scores = lista
            .GetProperty("itens")
            .EnumerateArray()
            .Select(i => i.GetProperty("score").GetInt32())
            .ToList();

        Assert.Equal(scores.OrderByDescending(s => s).ToList(), scores);
    }

    [Fact]
    public async Task A_paginacao_devolve_o_total_do_conjunto_inteiro()
    {
        await IngerirAsync("pag", quantidade: 5, cliente: "cli-pag");

        var primeira = await ListarAsync("tamanho=2&pagina=1");
        var terceira = await ListarAsync("tamanho=2&pagina=3");

        Assert.Equal(5, primeira.GetProperty("total").GetInt32());
        Assert.Equal(3, primeira.GetProperty("totalDePaginas").GetInt32());
        Assert.Equal(2, primeira.GetProperty("itens").GetArrayLength());
        Assert.Equal(1, terceira.GetProperty("itens").GetArrayLength());
    }

    // -----------------------------------------------------------------------
    // Detalhe consolidado
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_detalhe_reune_avaliacao_alerta_caso_e_veredito()
    {
        var alerta = await CenarioDeInvestigacao.GerarAlertaAsync(
            _cliente,
            _banco.StringDeConexao,
            _tenant,
            _integracao,
            _mensageria,
            "detalhe",
            Cancelamento);

        var casoId = await ResolverComoLegitimaAsync(alerta.Id);

        var detalhe = await GetAsync($"{Transacoes}/{alerta.TransacaoId}");

        // A avaliacao continua ali, explicando a decisao.
        Assert.NotEqual(JsonValueKind.Null, detalhe.GetProperty("avaliacao").ValueKind);

        // E agora o que a operacao fez depois dela.
        var alertas = detalhe.GetProperty("alertas").EnumerateArray().ToList();

        Assert.Single(alertas);
        Assert.Equal(casoId, alertas[0].GetProperty("casoId").GetGuid());
        Assert.False(string.IsNullOrEmpty(alertas[0].GetProperty("tituloDoCaso").GetString()));

        // O veredito humano pode contradizer o motor: decisao Revisar,
        // conclusao Legitima. E falso positivo legitimo, e nao defeito.
        Assert.Equal("Revisar", detalhe.GetProperty("avaliacao").GetProperty("decisao").GetString());
        Assert.Equal(
            nameof(ResultadoDaInvestigacao.Legitima),
            detalhe.GetProperty("veredito").GetString());
        Assert.Equal(casoId, detalhe.GetProperty("casoDoVeredito").GetGuid());
    }

    [Fact]
    public async Task O_detalhe_traz_o_numero_de_protocolo_da_requisicao()
    {
        // Fecha o fio da correlacao pela ponta que faltava: dada uma transacao
        // na tela, achar a requisicao que a criou no log do servidor.
        var transacoes = await IngerirAsync("protocolo", quantidade: 1, cliente: "cli-protocolo");

        var detalhe = await GetAsync($"{Transacoes}/{transacoes[0]}");

        var correlacao = detalhe.GetProperty("idDeCorrelacao").GetString();

        Assert.False(string.IsNullOrWhiteSpace(correlacao));

        // E o mesmo que a Outbox gravou: o identificador atravessa HTTP,
        // dominio e evento sem se perder no caminho.
        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenant.OrganizacaoId);

        var doEvento = await contexto.EventosDeSaida
            .AsNoTracking()
            .OrderByDescending(e => e.OcorridoEm)
            .Select(e => e.IdDeCorrelacao)
            .FirstAsync(Cancelamento);

        Assert.Equal(doEvento, correlacao);
    }

    [Fact]
    public async Task Transacao_sem_alerta_nem_caso_nao_inventa_contexto()
    {
        var transacoes = await IngerirAsync("limpa", quantidade: 1, cliente: "cli-limpo");

        var detalhe = await GetAsync($"{Transacoes}/{transacoes[0]}");

        Assert.Empty(detalhe.GetProperty("alertas").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, detalhe.GetProperty("veredito").ValueKind);
    }

    // -----------------------------------------------------------------------
    // Painel
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_painel_separa_o_que_chegou_do_que_foi_decidido()
    {
        await IngerirAsync("painel", quantidade: 3, cliente: "cli-painel");

        var painel = await GetAsync($"{Painel}?dias=7");

        Assert.Equal(3, painel.GetProperty("transacoesRecebidas").GetInt32());
        Assert.Equal(3, painel.GetProperty("transacoesAvaliadas").GetInt32());

        // As tres decisoes aparecem sempre, mesmo zeradas: uma faixa que some
        // da tela faria o analista achar que ela deixou de existir.
        var decisoes = painel.GetProperty("decisoes").EnumerateArray().ToList();

        Assert.Equal(3, decisoes.Count);
        Assert.Equal(
            3,
            decisoes.Single(d => d.GetProperty("chave").GetString() == "Permitir")
                .GetProperty("quantidade")
                .GetInt32());
    }

    [Fact]
    public async Task A_tendencia_sai_sem_buracos()
    {
        // Uma linha do tempo que pula dias faz um fim de semana parado
        // parecer um pico na segunda.
        await IngerirAsync("tend", quantidade: 1, cliente: "cli-tend");

        var painel = await GetAsync($"{Painel}?dias=7");

        var dias = painel.GetProperty("tendencia").EnumerateArray().ToList();

        Assert.Equal(8, dias.Count);
        Assert.Equal(
            dias.Select(d => d.GetProperty("dia").GetString()).Order(StringComparer.Ordinal),
            dias.Select(d => d.GetProperty("dia").GetString()));

        Assert.Equal(1, dias.Sum(d => d.GetProperty("total").GetInt32()));
    }

    [Fact]
    public async Task O_painel_conta_a_fila_humana_e_o_que_esta_parado()
    {
        var alerta = await CenarioDeInvestigacao.GerarAlertaAsync(
            _cliente,
            _banco.StringDeConexao,
            _tenant,
            _integracao,
            _mensageria,
            "fila",
            Cancelamento);

        var painel = await GetAsync($"{Painel}?dias=7");

        Assert.Equal(1, painel.GetProperty("alertasAbertos").GetInt32());
        Assert.Equal(0, painel.GetProperty("eventosPendentes").GetInt32());

        // Todos os tres status de caso aparecem, e nenhum caso existe ainda.
        var casos = painel.GetProperty("casos").EnumerateArray().ToList();

        Assert.Equal(3, casos.Count);
        Assert.All(casos, c => Assert.Equal(0, c.GetProperty("quantidade").GetInt32()));

        await ResolverComoLegitimaAsync(alerta.Id);

        var depois = await GetAsync($"{Painel}?dias=7");

        Assert.Equal(
            1,
            depois.GetProperty("casos").EnumerateArray()
                .Single(c => c.GetProperty("chave").GetString() == nameof(StatusDoCaso.Resolvido))
                .GetProperty("quantidade")
                .GetInt32());

        // Um caso resolvido hoje nunca conta como parado.
        Assert.Equal(0, depois.GetProperty("casosAntigos").GetInt32());
    }

    [Fact]
    public async Task Eventos_esperando_publicacao_aparecem_no_painel()
    {
        // E a medida direta de "a fila esta andando". Um numero que nao volta
        // a zero significa alerta que nao vai ser criado, e o analista
        // descobriria isso pelo silencio.
        await IngerirAsync("pendente", quantidade: 2, cliente: "cli-pendente");

        var antes = await GetAsync($"{Painel}?dias=7");
        Assert.Equal(2, antes.GetProperty("eventosPendentes").GetInt32());

        await _mensageria.RodarAteEsvaziarAsync(Cancelamento);

        var depois = await GetAsync($"{Painel}?dias=7");
        Assert.Equal(0, depois.GetProperty("eventosPendentes").GetInt32());
    }

    [Fact]
    public async Task Os_sinais_mais_frequentes_saem_do_maior_para_o_menor()
    {
        await IngerirRajadaAsync("freq");

        var painel = await GetAsync($"{Painel}?dias=7");

        var sinais = painel
            .GetProperty("sinaisMaisFrequentes")
            .EnumerateArray()
            .Select(s => s.GetProperty("acionamentos").GetInt32())
            .ToList();

        Assert.NotEmpty(sinais);
        Assert.Equal(sinais.OrderByDescending(s => s).ToList(), sinais);
    }

    // -----------------------------------------------------------------------
    // Metricas de regra
    // -----------------------------------------------------------------------

    [Fact]
    public async Task As_metricas_de_regra_cruzam_acionamento_com_veredito()
    {
        var alerta = await CenarioDeInvestigacao.GerarAlertaAsync(
            _cliente,
            _banco.StringDeConexao,
            _tenant,
            _integracao,
            _mensageria,
            "metrica",
            Cancelamento);

        await ResolverComoLegitimaAsync(alerta.Id);

        var metricas = await GetAsync($"{Painel}/regras?dias=7");

        var linhas = metricas.EnumerateArray().ToList();

        Assert.NotEmpty(linhas);

        // A transacao investigada acionou o dispositivo novo e a divergencia
        // geografica; as duas contam a mesma conclusao humana, porque o
        // veredito e por transacao.
        var comVeredito = linhas.Where(l => l.GetProperty("legitima").GetInt32() > 0).ToList();

        Assert.NotEmpty(comVeredito);

        Assert.All(linhas, linha =>
        {
            var total = linha.GetProperty("fraudeConfirmada").GetInt32()
                + linha.GetProperty("legitima").GetInt32()
                + linha.GetProperty("inconclusiva").GetInt32()
                + linha.GetProperty("semResultadoConhecido").GetInt32();

            // O denominador fecha: cada acionamento cai em exatamente uma das
            // quatro colunas. E o que torna a leitura honesta.
            Assert.Equal(linha.GetProperty("acionamentos").GetInt32(), total);
        });

        // A resposta nao traz taxa nenhuma calculada — nem precisao, nem
        // recall. Os dados nao sustentam isso.
        var bruto = JsonSerializer.Serialize(metricas);

        Assert.DoesNotContain("precisao", bruto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recall", bruto, StringComparison.OrdinalIgnoreCase);
    }

    // =======================================================================
    // Apoio
    // =======================================================================

    private static int Total(JsonElement pagina) => pagina.GetProperty("total").GetInt32();

    private static IEnumerable<Guid> Ids(JsonElement pagina) =>
        pagina.GetProperty("itens").EnumerateArray().Select(i => i.GetProperty("id").GetGuid());

    private Task<JsonElement> ListarAsync(string consulta) =>
        GetAsync(consulta.Length == 0 ? Transacoes : $"{Transacoes}?{consulta}");

    private async Task<IReadOnlyList<Guid>> IngerirAsync(
        string prefixo,
        int quantidade,
        string cliente)
    {
        var inicio = DateTimeOffset.UtcNow.AddMinutes(-quantidade * 60);
        var ids = new List<Guid>();

        for (var i = 0; i < quantidade; i++)
        {
            using var resposta = await CenarioDeIngestao.EnviarAsync(
                _cliente,
                _integracao.Chave,
                $"idem-{Guid.NewGuid():N}",
                CenarioDeIngestao.Corpo(
                    identificadorExterno: $"{prefixo}-{i}-{Guid.NewGuid():N}"[..24],
                    valor: 100m,
                    clienteExternoId: cliente,
                    // Espacadas em horas: em minutos a regra de velocidade
                    // acionaria e o score deixaria de ser o que o teste afirma.
                    ocorridaEm: inicio.AddMinutes(i * 60),
                    fingerprintDoDispositivo: "disp-de-casa",
                    paisDeOrigem: "BR"),
                Cancelamento);

            resposta.EnsureSuccessStatusCode();

            ids.Add(await CenarioDeIngestao.IdDaTransacaoAsync(resposta));
        }

        return ids;
    }

    /// <summary>Uma transacao que o motor bloqueia, para os filtros terem contraste.</summary>
    private async Task IngerirBloqueioAsync(string prefixo)
    {
        var cliente = $"cli-{prefixo}-{Guid.NewGuid():N}"[..24];
        var inicio = DateTimeOffset.UtcNow.AddDays(-10);

        for (var i = 0; i < 3; i++)
        {
            using var historico = await CenarioDeIngestao.EnviarAsync(
                _cliente,
                _integracao.Chave,
                $"idem-{Guid.NewGuid():N}",
                CenarioDeIngestao.Corpo(
                    identificadorExterno: $"{prefixo}-h{i}-{Guid.NewGuid():N}"[..24],
                    valor: 100m,
                    clienteExternoId: cliente,
                    ocorridaEm: inicio.AddDays(i),
                    fingerprintDoDispositivo: "disp-de-casa",
                    paisDeOrigem: "BR"),
                Cancelamento);

            historico.EnsureSuccessStatusCode();
        }

        using var alvo = await CenarioDeIngestao.EnviarAsync(
            _cliente,
            _integracao.Chave,
            $"idem-{Guid.NewGuid():N}",
            CenarioDeIngestao.Corpo(
                identificadorExterno: $"{prefixo}-alvo-{Guid.NewGuid():N}"[..24],
                valor: 900m,
                clienteExternoId: cliente,
                ocorridaEm: DateTimeOffset.UtcNow.AddMinutes(-1),
                fingerprintDoDispositivo: "disp-novo",
                paisDeOrigem: "PT"),
            Cancelamento);

        alvo.EnsureSuccessStatusCode();
    }

    /// <summary>Transacoes seguidas do mesmo cliente, para a regra de velocidade acionar.</summary>
    private async Task IngerirRajadaAsync(string prefixo)
    {
        var cliente = $"cli-{prefixo}-{Guid.NewGuid():N}"[..24];
        var inicio = DateTimeOffset.UtcNow.AddMinutes(-5);

        for (var i = 0; i < 5; i++)
        {
            using var resposta = await CenarioDeIngestao.EnviarAsync(
                _cliente,
                _integracao.Chave,
                $"idem-{Guid.NewGuid():N}",
                CenarioDeIngestao.Corpo(
                    identificadorExterno: $"{prefixo}-{i}-{Guid.NewGuid():N}"[..24],
                    valor: 100m,
                    clienteExternoId: cliente,
                    ocorridaEm: inicio.AddSeconds(i * 10),
                    fingerprintDoDispositivo: "disp-de-casa",
                    paisDeOrigem: "BR"),
                Cancelamento);

            resposta.EnsureSuccessStatusCode();
        }
    }

    private async Task<Guid> ResolverComoLegitimaAsync(Guid alertaId)
    {
        var caso = await LerAsync(await PostAsync(
            "/api/casos",
            new { titulo = "Investigacao do console", alertasIds = new[] { alertaId } }));

        var casoId = caso.GetProperty("id").GetGuid();

        var assumido = await LerAsync(await PostAsync(
            $"/api/casos/{casoId}/assumir",
            new { versao = caso.GetProperty("versao").GetInt32() }));

        using var resolvido = await PostAsync(
            $"/api/casos/{casoId}/resolucao",
            new
            {
                resultado = nameof(ResultadoDaInvestigacao.Legitima),
                versao = assumido.GetProperty("versao").GetInt32(),
            });

        resolvido.EnsureSuccessStatusCode();

        return casoId;
    }

    private async Task<HttpResponseMessage> PostAsync(
        string caminho,
        object corpo,
        PerfilDeUsuario perfil = PerfilDeUsuario.AnalistaDeFraude)
    {
        var token = await TokenAsync(perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Post, caminho, token);
        requisicao.Content = JsonContent.Create(corpo);

        return await _cliente.SendAsync(requisicao, Cancelamento);
    }

    private async Task<JsonElement> GetAsync(
        string caminho,
        PerfilDeUsuario perfil = PerfilDeUsuario.AnalistaDeFraude)
    {
        var token = await TokenAsync(perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Get, caminho, token);
        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        var corpo = await resposta.Content.ReadAsStringAsync(Cancelamento);

        // O corpo entra na mensagem de falha de proposito: um 500 seco numa
        // consulta obriga a reproduzir o cenario so para descobrir o que
        // quebrou, e o log do host de teste ja tem a resposta.
        Assert.True(
            resposta.IsSuccessStatusCode,
            $"{caminho} respondeu {(int)resposta.StatusCode}: {corpo} || "
                + string.Join(" || ", _fabrica.Registros.Where(r => r.Contains("Exception", StringComparison.Ordinal)).TakeLast(1)).Replace(System.Environment.NewLine, " ~ ", StringComparison.Ordinal));

        return JsonDocument.Parse(corpo).RootElement.Clone();
    }

    private async Task<string> TokenAsync(PerfilDeUsuario perfil)
    {
        if (_tokens.TryGetValue(perfil, out var existente))
        {
            return existente;
        }

        var token = await CenarioDeIngestao.TokenDeAsync(_cliente, _tenant, perfil, Cancelamento);
        _tokens[perfil] = token;

        return token;
    }

    private static async Task<JsonElement> LerAsync(HttpResponseMessage resposta)
    {
        var corpo = await resposta.Content.ReadAsStringAsync(Cancelamento);

        return JsonDocument.Parse(corpo).RootElement.Clone();
    }
}
