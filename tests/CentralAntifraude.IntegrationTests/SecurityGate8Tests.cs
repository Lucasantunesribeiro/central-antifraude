using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// Security Gate 8 — quem pode mudar o que o motor executa.
///
/// Esta e a primeira fase em que uma pessoa altera o **comportamento do
/// produto**, e nao apenas dados operacionais. Os riscos sao proprios:
///
/// 1. **perfil errado publicando regra**, porque uma regra publicada alcanca
///    toda transacao que entrar depois;
/// 2. **configuracao arbitraria**, porque o catalogo fechado e o que impede o
///    banco de virar codigo executavel (`CLAUDE.md` secao 21);
/// 3. **edicao do que ja foi publicado**, porque e ela que apagaria a
///    explicabilidade historica;
/// 4. **duas publicacoes ao mesmo tempo**, porque a versao vigente e uma so.
///
/// Cobre os oito itens da secao 8.8 do `ROADMAP.md`.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class SecurityGate8Tests : IAsyncLifetime
{
    private const string Caminho = "/api/regras";

    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;
    private HttpClient _cliente = null!;
    private TenantDeTeste _tenantA = null!;
    private TenantDeTeste _tenantB = null!;

    private readonly Dictionary<(string Codigo, PerfilDeUsuario Perfil), string> _tokens = [];

    public SecurityGate8Tests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao))
        {
            await contexto.Database.MigrateAsync(Cancelamento);
        }

        var sufixo = Guid.NewGuid().ToString("N")[..6];

        _tenantA = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"sg8a-{sufixo}",
            Cancelamento);

        _tenantB = await CenarioDeIdentidade.CriarTenantAsync(
            _banco.StringDeConexao,
            $"sg8b-{sufixo}",
            Cancelamento);

        _fabrica = new FabricaDaApi(_banco.StringDeConexao);
        _cliente = _fabrica.CriarClienteSemCookieAutomatico();
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
    // 1. Analista publicando regra. 2. Auditor editando.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(PerfilDeUsuario.AnalistaDeFraude)]
    [InlineData(PerfilDeUsuario.Auditor)]
    public async Task Quem_nao_e_supervisao_nao_alcanca_nenhuma_rota_de_regra(PerfilDeUsuario perfil)
    {
        // Gerir regras e supervisao (`CLAUDE.md` secao 8.2). O analista opera
        // alertas e casos; o auditor le. Nenhum dos dois muda o motor — e a
        // recusa nao depende de esconder botao.
        var regraId = await RegraIdAsync(_tenantA, TipoDeRegra.NovoDispositivo);
        var versao = (await ObterAsync(_tenantA, regraId)).GetProperty("versao").GetInt32();

        var tentativas = new (HttpMethod Metodo, string Caminho, object? Corpo)[]
        {
            (HttpMethod.Get, $"{Caminho}/tipos", null),
            (HttpMethod.Get, $"{Caminho}/gestao", null),
            (HttpMethod.Get, $"{Caminho}/{regraId}", null),
            (HttpMethod.Post, Caminho, new
            {
                tipo = nameof(TipoDeRegra.NovoDispositivo),
                nome = "Invasora",
                configuracao = new Dictionary<string, decimal> { ["minimoDeTransacoesNoHistorico"] = 3 },
                pontos = 10,
            }),
            (HttpMethod.Put, $"{Caminho}/{regraId}/rascunho", new
            {
                nome = "Renomeada",
                configuracao = new Dictionary<string, decimal> { ["minimoDeTransacoesNoHistorico"] = 9 },
                pontos = 50,
                versao,
            }),
            (HttpMethod.Delete, $"{Caminho}/{regraId}/rascunho?versao={versao}", null),
            (HttpMethod.Post, $"{Caminho}/{regraId}/publicacao", new { versao }),
            (HttpMethod.Post, $"{Caminho}/{regraId}/ativacao", new { ativa = false, versao }),
            (HttpMethod.Post, $"{Caminho}/perfil/limiares", new
            {
                limiarDeRevisao = 5,
                limiarDeBloqueio = 10,
                numeroDaVersaoVigente = 1,
            }),
        };

        foreach (var (metodo, caminho, corpo) in tentativas)
        {
            using var resposta = await EnviarAsync(_tenantA, perfil, metodo, caminho, corpo);

            Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        }

        // E nada mudou no banco: nem versao da regra, nem versao do perfil.
        var depois = await ObterAsync(_tenantA, regraId);

        Assert.Equal(versao, depois.GetProperty("versao").GetInt32());
        Assert.Equal(1, depois.GetProperty("numeroDaVersaoVigente").GetInt32());
    }

    [Fact]
    public async Task Sem_autenticacao_nenhuma_rota_de_regra_responde()
    {
        foreach (var caminho in new[] { $"{Caminho}/tipos", $"{Caminho}/gestao" })
        {
            using var requisicao = new HttpRequestMessage(HttpMethod.Get, new Uri(caminho, UriKind.Relative));
            using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        }
    }

    // -----------------------------------------------------------------------
    // 3. Tenant B acessando regra do tenant A
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Regra_de_outro_tenant_nao_existe_daqui()
    {
        // 404, e nao 403: um 403 confirmaria que o identificador e valido em
        // algum lugar (`CLAUDE.md` secao 52).
        var regraDeA = await RegraIdAsync(_tenantA, TipoDeRegra.VelocidadePorCliente);

        using var leitura = await EnviarAsync(
            _tenantB,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Get,
            $"{Caminho}/{regraDeA}",
            null);

        Assert.Equal(HttpStatusCode.NotFound, leitura.StatusCode);

        using var publicacao = await EnviarAsync(
            _tenantB,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Post,
            $"{Caminho}/{regraDeA}/publicacao",
            new { versao = 1 });

        Assert.Equal(HttpStatusCode.NotFound, publicacao.StatusCode);

        using var ativacao = await EnviarAsync(
            _tenantB,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Post,
            $"{Caminho}/{regraDeA}/ativacao",
            new { ativa = false, versao = 1 });

        Assert.Equal(HttpStatusCode.NotFound, ativacao.StatusCode);

        // A lista de B nao inclui a regra de A.
        var gestaoDeB = await ListarGestaoAsync(_tenantB);

        Assert.DoesNotContain(
            gestaoDeB.EnumerateArray(),
            r => r.GetProperty("id").GetGuid() == regraDeA);

        // E a regra de A continua exatamente como estava.
        var deA = await ObterAsync(_tenantA, regraDeA);

        Assert.True(deA.GetProperty("ativa").GetBoolean());
        Assert.Equal(1, deA.GetProperty("numeroDaVersaoVigente").GetInt32());
    }

    [Fact]
    public async Task Publicar_no_tenant_A_nao_toca_no_perfil_do_tenant_B()
    {
        // O vazamento mais silencioso possivel seria a versao de perfil de um
        // cliente passar a incluir regra de outro: o resultado sairia
        // plausivel e ninguem notaria.
        var perfilDeBAntes = await PerfilVigenteAsync(_tenantB);

        var regraDeA = await RegraIdAsync(_tenantA, TipoDeRegra.DivergenciaGeografica);
        var versao = (await ObterAsync(_tenantA, regraDeA)).GetProperty("versao").GetInt32();

        await SalvarRascunhoAsync(
            _tenantA,
            regraDeA,
            "Divergencia geografica",
            new() { ["minimoDeTransacoesNoHistorico"] = 8 },
            pontos: 44,
            versao);

        var perfilDeBDepois = await PerfilVigenteAsync(_tenantB);

        Assert.Equal(
            perfilDeBAntes.GetProperty("numero").GetInt32(),
            perfilDeBDepois.GetProperty("numero").GetInt32());

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantB.OrganizacaoId);

        // Nenhuma regra de B carrega rascunho: o que foi escrito ficou em A.
        var comRascunho = await contexto.Regras
            .CountAsync(r => r.PontosEmRascunho != null, Cancelamento);

        Assert.Equal(0, comRascunho);
    }

    // -----------------------------------------------------------------------
    // 4. Edicao de versao publicada
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Nao_existe_rota_que_altere_uma_versao_publicada()
    {
        // A imutabilidade nao e uma convencao: nao ha metodo de dominio nem
        // rota HTTP. Um `PUT /regras/{id}` aceitando o objeto inteiro
        // convidaria ao mass assignment — bastaria mandar
        // `numeroDaUltimaVersao`.
        var regraId = await RegraIdAsync(_tenantA, TipoDeRegra.NovoDispositivo);

        var detalhe = await ObterAsync(_tenantA, regraId);
        var versaoId = detalhe.GetProperty("versoes")[0].GetProperty("id").GetGuid();

        var rotasInexistentes = new (HttpMethod Metodo, string Caminho)[]
        {
            (HttpMethod.Put, $"{Caminho}/{regraId}"),
            (HttpMethod.Patch, $"{Caminho}/{regraId}"),
            (HttpMethod.Delete, $"{Caminho}/{regraId}"),
            (HttpMethod.Put, $"{Caminho}/{regraId}/versoes/{versaoId}"),
            (HttpMethod.Delete, $"{Caminho}/{regraId}/versoes/{versaoId}"),
            (HttpMethod.Put, $"{Caminho}/perfil"),
            (HttpMethod.Delete, $"{Caminho}/perfil"),
        };

        foreach (var (metodo, caminho) in rotasInexistentes)
        {
            using var resposta = await EnviarAsync(
                _tenantA,
                PerfilDeUsuario.SupervisorDeFraude,
                metodo,
                caminho,
                metodo == HttpMethod.Delete ? null : new { pontos = 99 });

            Assert.True(
                resposta.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{metodo} {caminho} devolveu {(int)resposta.StatusCode}.");
        }

        // E a versao continua com o peso original.
        var depois = await ObterAsync(_tenantA, regraId);

        Assert.Equal(20, depois.GetProperty("versoes")[0].GetProperty("pontos").GetInt32());
    }

    // -----------------------------------------------------------------------
    // 5. Configuracao invalida. 6. Tipo desconhecido. 7. Campo arbitrario.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0, 10, "maximoDeTransacoes")]
    [InlineData(3, 0, "janelaEmMinutos")]
    [InlineData(3, 5000, "janelaEmMinutos")]
    [InlineData(1001, 10, "maximoDeTransacoes")]
    public async Task Configuracao_fora_da_faixa_e_recusada_com_o_campo_na_mensagem(
        int maximo,
        int janela,
        string campoEsperado)
    {
        // A faixa aparece na mensagem porque o Supervisor precisa saber qual
        // campo esta errado — nao apenas que "a configuracao e invalida".
        using var resposta = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Post,
            Caminho,
            new
            {
                tipo = nameof(TipoDeRegra.VelocidadePorCliente),
                nome = $"Fora da faixa {maximo}-{janela}",
                configuracao = new Dictionary<string, decimal>
                {
                    ["maximoDeTransacoes"] = maximo,
                    ["janelaEmMinutos"] = janela,
                },
                pontos = 10,
            });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);

        var corpo = await resposta.Content.ReadAsStringAsync(Cancelamento);

        Assert.Contains(campoEsperado, corpo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RegraSecreta")]
    [InlineData("velocidadeporcliente")]
    [InlineData("1")]
    [InlineData("")]
    public async Task Tipo_de_regra_desconhecido_e_recusado(string tipo)
    {
        // O catalogo e fechado e a comparacao e exata. Aceitar variacao de
        // caixa faria o vocabulario deixar de ser fechado na pratica.
        using var resposta = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Post,
            Caminho,
            new
            {
                tipo,
                nome = $"Tipo invalido {Guid.NewGuid():N}"[..20],
                configuracao = new Dictionary<string, decimal> { ["qualquer"] = 1 },
                pontos = 10,
            });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Campo_arbitrario_na_configuracao_e_recusado()
    {
        // Ignorar em silencio faria quem enviou acreditar que aquele numero
        // foi usado pelo motor.
        using var resposta = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Post,
            Caminho,
            new
            {
                tipo = nameof(TipoDeRegra.NovoDispositivo),
                nome = "Com campo extra",
                configuracao = new Dictionary<string, decimal>
                {
                    ["minimoDeTransacoesNoHistorico"] = 3,
                    ["bonusSecreto"] = 500,
                },
                pontos = 10,
            });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);

        var corpo = await resposta.Content.ReadAsStringAsync(Cancelamento);

        Assert.Contains("bonusSecreto", corpo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("organizacaoId")]
    [InlineData("ativa")]
    [InlineData("numeroDaUltimaVersao")]
    [InlineData("id")]
    [InlineData("criadaEm")]
    public async Task Campo_interno_no_corpo_da_criacao_e_recusado(string campo)
    {
        // JSON estrito: campo desconhecido nao e ignorado em silencio
        // (`CLAUDE.md` secoes 53 e 54). Ignorar faria quem mandou acreditar
        // que o campo foi aceito.
        var corpo = $$"""
            {
              "tipo": "NovoDispositivo",
              "nome": "Tentativa de {{campo}}",
              "configuracao": { "minimoDeTransacoesNoHistorico": 3 },
              "pontos": 10,
              "{{campo}}": "{{Guid.NewGuid()}}"
            }
            """;

        var token = await TokenAsync(_tenantA, PerfilDeUsuario.SupervisorDeFraude);

        using var requisicao = CenarioDeIdentidade.Autenticada(HttpMethod.Post, Caminho, token);
        requisicao.Content = new StringContent(corpo, Encoding.UTF8, "application/json");

        using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Regra <b>forte</b>")]
    [InlineData("ab")]
    public async Task Nome_de_regra_invalido_e_recusado(string nome)
    {
        // O nome e exibido na lista, no detalhe e na trilha de auditoria.
        // Vale a mesma regra do resto do produto: recusar, nunca limpar em
        // silencio.
        using var resposta = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Post,
            Caminho,
            new
            {
                tipo = nameof(TipoDeRegra.NovoDispositivo),
                nome,
                configuracao = new Dictionary<string, decimal> { ["minimoDeTransacoesNoHistorico"] = 3 },
                pontos = 10,
            });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    public async Task Peso_fora_da_faixa_e_recusado(int pontos)
    {
        using var resposta = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Post,
            Caminho,
            new
            {
                tipo = nameof(TipoDeRegra.NovoDispositivo),
                nome = $"Peso {pontos}",
                configuracao = new Dictionary<string, decimal> { ["minimoDeTransacoesNoHistorico"] = 3 },
                pontos,
            });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // 8. Concorrencia de publicacao
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Agir_com_uma_versao_velha_da_regra_e_recusado()
    {
        // Primeira camada: a versao que o cliente leu. Ela existe para dar um
        // conflito claro sem tocar no banco.
        var regraId = await RegraIdAsync(_tenantA, TipoDeRegra.NovoDispositivo);
        var versao = (await ObterAsync(_tenantA, regraId)).GetProperty("versao").GetInt32();

        await SalvarRascunhoAsync(
            _tenantA,
            regraId,
            "Dispositivo novo",
            new() { ["minimoDeTransacoesNoHistorico"] = 9 },
            pontos: 21,
            versao);

        using var atrasada = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Post,
            $"{Caminho}/{regraId}/publicacao",
            new { versao });

        Assert.Equal(HttpStatusCode.Conflict, atrasada.StatusCode);
        Assert.Equal("versao_desatualizada", await CodigoAsync(atrasada));
    }

    [Fact]
    public async Task Duas_publicacoes_simultaneas_da_mesma_regra_produzem_uma_versao_so()
    {
        // Dois supervisores com a mesma tela aberta apertam publicar. As duas
        // requisicoes carregam a MESMA versao da regra, entao a recusa nao
        // depende de quem chegou primeiro no banco: a checagem da aplicacao ja
        // separa as duas de forma deterministica.
        //
        // Sem ela, a regra ganharia duas versoes com o mesmo conteudo e o
        // perfil seria sucedido duas vezes por uma unica decisao humana.
        var regraId = await RegraIdAsync(_tenantA, TipoDeRegra.NovoDispositivo);

        var versao = await SalvarRascunhoAsync(
            _tenantA,
            regraId,
            "Dispositivo novo",
            new() { ["minimoDeTransacoesNoHistorico"] = 9 },
            pontos: 21,
            (await ObterAsync(_tenantA, regraId)).GetProperty("versao").GetInt32());

        var token = await TokenAsync(_tenantA, PerfilDeUsuario.SupervisorDeFraude);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            using var requisicao = CenarioDeIdentidade.Autenticada(
                HttpMethod.Post,
                $"{Caminho}/{regraId}/publicacao",
                token);

            requisicao.Content = JsonContent.Create(new { versao });

            using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

            return resposta.StatusCode;
        }));

        Assert.Equal(1, respostas.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(1, respostas.Count(s => s == HttpStatusCode.Conflict));

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        // Uma versao nova, e nao duas.
        var versoes = await contexto.VersoesDeRegra
            .Where(v => v.RegraId == regraId)
            .CountAsync(Cancelamento);

        Assert.Equal(2, versoes);
    }

    [Fact]
    public async Task Publicacoes_paralelas_nunca_deixam_duas_versoes_de_perfil_com_o_mesmo_numero()
    {
        // Aqui as duas publicacoes sao de regras DIFERENTES, entao as duas sao
        // legitimas: cada uma carrega a versao correta da sua regra. O que
        // colide e o **numero da proxima versao de perfil**, que as duas
        // calculam a partir da mesma vigente.
        //
        // A afirmacao do teste e o invariante, e nao a divisao entre 201 e 409:
        // dependendo de quanto as duas requisicoes se sobrepoem, ora as duas
        // passam em sequencia, ora uma perde a corrida. As duas saidas sao
        // corretas. O que **nunca** pode acontecer e a numeracao duplicar — e e
        // o indice unico `(perfil, numero)` que garante isso.
        var primeira = await RegraIdAsync(_tenantA, TipoDeRegra.NovoDispositivo);
        var segunda = await RegraIdAsync(_tenantA, TipoDeRegra.DivergenciaGeografica);

        var versaoPrimeira = await SalvarRascunhoAsync(
            _tenantA,
            primeira,
            "Dispositivo novo",
            new() { ["minimoDeTransacoesNoHistorico"] = 9 },
            pontos: 21,
            (await ObterAsync(_tenantA, primeira)).GetProperty("versao").GetInt32());

        var versaoSegunda = await SalvarRascunhoAsync(
            _tenantA,
            segunda,
            "Divergencia geografica",
            new() { ["minimoDeTransacoesNoHistorico"] = 9 },
            pontos: 26,
            (await ObterAsync(_tenantA, segunda)).GetProperty("versao").GetInt32());

        var perfilAntes = (await PerfilVigenteAsync(_tenantA)).GetProperty("numero").GetInt32();

        var token = await TokenAsync(_tenantA, PerfilDeUsuario.SupervisorDeFraude);

        var pedidos = new[] { (primeira, versaoPrimeira), (segunda, versaoSegunda) };

        // Task.Run para que as duas saiam de threads diferentes: sem isso, a
        // primeira costuma terminar antes de a segunda comecar, e o teste
        // deixaria de exercitar a corrida.
        var respostas = await Task.WhenAll(pedidos.Select(pedido => Task.Run(
            async () =>
            {
                using var requisicao = CenarioDeIdentidade.Autenticada(
                    HttpMethod.Post,
                    $"{Caminho}/{pedido.Item1}/publicacao",
                    token);

                requisicao.Content = JsonContent.Create(new { versao = pedido.Item2 });

                using var resposta = await _cliente.SendAsync(requisicao, Cancelamento);

                return resposta.StatusCode;
            },
            Cancelamento)));

        var criadas = respostas.Count(s => s == HttpStatusCode.Created);

        Assert.Equal(2, criadas + respostas.Count(s => s == HttpStatusCode.Conflict));
        Assert.True(criadas >= 1, "Ao menos uma publicacao deveria ter passado.");

        await using var contexto = CenarioDeIdentidade.CriarContextoComoTenant(
            _banco.StringDeConexao,
            _tenantA.OrganizacaoId);

        var numeros = await contexto.VersoesDePerfilDeRisco
            .Select(v => v.Numero)
            .ToListAsync(Cancelamento);

        Assert.Equal(numeros.Count, numeros.Distinct().Count());

        // O perfil vigente avancou exatamente o numero de publicacoes que
        // passaram: nenhuma sucessao se perdeu, nenhuma aconteceu duas vezes.
        var perfilDepois = (await PerfilVigenteAsync(_tenantA)).GetProperty("numero").GetInt32();

        Assert.Equal(perfilAntes + criadas, perfilDepois);
    }

    [Fact]
    public async Task Limiares_com_versao_de_perfil_velha_sao_recusados()
    {
        var perfil = await PerfilVigenteAsync(_tenantA);
        var numero = perfil.GetProperty("numero").GetInt32();

        using var primeira = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Post,
            $"{Caminho}/perfil/limiares",
            new { limiarDeRevisao = 30, limiarDeBloqueio = 80, numeroDaVersaoVigente = numero });

        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);

        using var segunda = await EnviarAsync(
            _tenantA,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Post,
            $"{Caminho}/perfil/limiares",
            new { limiarDeRevisao = 20, limiarDeBloqueio = 90, numeroDaVersaoVigente = numero });

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        Assert.Equal("versao_desatualizada", await CodigoAsync(segunda));

        // Os limiares que valem sao os da primeira publicacao.
        Assert.Equal(30, (await PerfilVigenteAsync(_tenantA)).GetProperty("limiarDeRevisao").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Item adicional: a leitura operacional continua aberta a todos
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(PerfilDeUsuario.AnalistaDeFraude)]
    [InlineData(PerfilDeUsuario.Auditor)]
    public async Task Todo_perfil_continua_lendo_as_regras_vigentes(PerfilDeUsuario perfil)
    {
        // O analista precisa do catalogo para entender o proprio score, e o
        // auditor precisa dele para conferir uma decisao. Fechar a leitura
        // junto com a escrita teria sido simples e errado.
        using var resposta = await EnviarAsync(_tenantA, perfil, HttpMethod.Get, $"{Caminho}/perfil", null);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Apoio
    // -----------------------------------------------------------------------

    private async Task<HttpResponseMessage> EnviarAsync(
        TenantDeTeste tenant,
        PerfilDeUsuario perfil,
        HttpMethod metodo,
        string caminho,
        object? corpo)
    {
        var token = await TokenAsync(tenant, perfil);

        using var requisicao = CenarioDeIdentidade.Autenticada(metodo, caminho, token);

        if (corpo is not null)
        {
            requisicao.Content = JsonContent.Create(corpo);
        }

        return await _cliente.SendAsync(requisicao, Cancelamento);
    }

    private async Task<JsonElement> SupervisorGetAsync(TenantDeTeste tenant, string caminho)
    {
        using var resposta = await EnviarAsync(
            tenant,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Get,
            caminho,
            null);

        resposta.EnsureSuccessStatusCode();

        return JsonDocument
            .Parse(await resposta.Content.ReadAsStringAsync(Cancelamento))
            .RootElement
            .Clone();
    }

    private Task<JsonElement> ListarGestaoAsync(TenantDeTeste tenant) =>
        SupervisorGetAsync(tenant, $"{Caminho}/gestao");

    private Task<JsonElement> ObterAsync(TenantDeTeste tenant, Guid regraId) =>
        SupervisorGetAsync(tenant, $"{Caminho}/{regraId}");

    private Task<JsonElement> PerfilVigenteAsync(TenantDeTeste tenant) =>
        SupervisorGetAsync(tenant, $"{Caminho}/perfil");

    private async Task<Guid> RegraIdAsync(TenantDeTeste tenant, TipoDeRegra tipo)
    {
        var regras = await ListarGestaoAsync(tenant);

        return regras
            .EnumerateArray()
            .First(r => r.GetProperty("tipo").GetString() == tipo.ToString())
            .GetProperty("id")
            .GetGuid();
    }

    private async Task<int> SalvarRascunhoAsync(
        TenantDeTeste tenant,
        Guid regraId,
        string nome,
        Dictionary<string, decimal> configuracao,
        int pontos,
        int versao)
    {
        using var resposta = await EnviarAsync(
            tenant,
            PerfilDeUsuario.SupervisorDeFraude,
            HttpMethod.Put,
            $"{Caminho}/{regraId}/rascunho",
            new { nome, configuracao, pontos, versao });

        resposta.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(Cancelamento));

        return json.RootElement.GetProperty("versao").GetInt32();
    }

    private static async Task<string?> CodigoAsync(HttpResponseMessage resposta)
    {
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(Cancelamento));

        return json.RootElement.TryGetProperty("codigo", out var codigo) ? codigo.GetString() : null;
    }

    private async Task<string> TokenAsync(TenantDeTeste tenant, PerfilDeUsuario perfil)
    {
        var chave = (tenant.Codigo, perfil);

        if (_tokens.TryGetValue(chave, out var guardado))
        {
            return guardado;
        }

        var token = await CenarioDeIngestao.TokenDeAsync(_cliente, tenant, perfil, Cancelamento);

        _tokens[chave] = token;

        return token;
    }
}
