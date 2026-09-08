using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.IntegrationTests.Infra;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CentralAntifraude.IntegrationTests;

/// <summary>
/// A demonstração conta as histórias que o produto precisa contar. (ROADMAP 13.2)
///
/// **Por que isto é um teste, e não uma conferência a olho.** O seed narrativo
/// tem uma propriedade incomum: ele não escreve as decisões, ele as PEDE ao
/// motor de risco. Isso é o que impede a demonstração de virar mentira quando
/// alguém mexer num peso de regra — e é também o que faz dela algo que pode
/// quebrar sozinha. Um ajuste de catálogo que zere o falso positivo não
/// apareceria em nenhum outro teste da suíte: a decisão continuaria correta, e
/// só a narrativa deixaria de existir.
///
/// O que se afirma aqui, então, não é "o motor está certo" — dez fases já
/// cuidam disso. É que **as seis histórias continuam acontecendo**.
/// </summary>
[Collection(ColecaoDoBanco.Nome)]
public sealed class SeedNarrativoTests : IAsyncLifetime
{
    private readonly FixtureDoBanco _banco;
    private FabricaDaApi _fabrica = null!;

    public SeedNarrativoTests(FixtureDoBanco banco) => _banco = banco;

    private static CancellationToken Cancelamento => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);
        await contexto.Database.MigrateAsync(Cancelamento);

        // O host de teste roda em Production e não semeia. Aqui o seed é
        // chamado a mão, com a senha configurada, exatamente como o
        // `Program.cs` faz em desenvolvimento.
        _fabrica = new FabricaDaApi(
            _banco.StringDeConexao,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Seed:SenhaPadrao"] = "senha-de-teste-do-seed",
            });

        using var escopo = _fabrica.Services.CreateScope();

        await CentralAntifraude.Infrastructure.Persistencia.SeedDeDesenvolvimento.ExecutarAsync(
            escopo.ServiceProvider,
            _fabrica.Services.GetRequiredService<IConfiguration>(),
            Cancelamento);
    }

    public async ValueTask DisposeAsync()
    {
        if (_fabrica is not null)
        {
            await _fabrica.DisposeAsync();
        }
    }

    [Fact]
    public async Task As_tres_decisoes_aparecem_e_a_maioria_e_permitir()
    {
        var decisoes = await ContarDecisoesAsync();

        // As três precisam existir: uma demonstração sem `Bloquear` não mostra
        // o produto, e uma sem `Permitir` descreveria um motor quebrado.
        Assert.Contains(Decisao.Permitir, decisoes.Keys);
        Assert.Contains(Decisao.Revisar, decisoes.Keys);
        Assert.Contains(Decisao.Bloquear, decisoes.Keys);

        // E a proporção precisa ser plausível. Antifraude que barra metade do
        // movimento não é antifraude, é prejuízo — e um painel de demonstração
        // com 50% de bloqueio ensina a coisa errada a quem o vê.
        var total = decisoes.Values.Sum();

        Assert.True(
            decisoes[Decisao.Permitir] > total / 2,
            $"Permitir deveria ser a maioria; foi {decisoes[Decisao.Permitir]} de {total}.");
    }

    [Fact]
    public async Task A_compra_atipica_soma_tres_sinais_e_termina_em_bloquear()
    {
        var avaliacao = await AvaliacaoDeAsync("demo-b-compra-suspeita");

        Assert.Equal(Decisao.Bloquear, avaliacao.Decisao);

        // Valor acima do histórico, dispositivo novo e divergência geográfica.
        // O teste nomeia os três: se um deixar de acionar, o score pode até
        // continuar alto por outro caminho, e a história deixaria de ser a que
        // a documentação descreve.
        var tipos = avaliacao.Sinais.Select(s => s.Tipo).ToHashSet();

        Assert.Contains(TipoDeRegra.ValorAcimaDoHistorico, tipos);
        Assert.Contains(TipoDeRegra.NovoDispositivo, tipos);
        Assert.Contains(TipoDeRegra.DivergenciaGeografica, tipos);
    }

    [Fact]
    public async Task Um_sinal_sozinho_nao_alcanca_o_limiar_e_a_transacao_passa()
    {
        var avaliacao = await AvaliacaoDeAsync("demo-d-de-outro-pais");

        // Um sinal isolado é o que permite explicar uma regra sem que outras
        // três estejam somando junto.
        var sinal = Assert.Single(avaliacao.Sinais);
        Assert.Equal(TipoDeRegra.DivergenciaGeografica, sinal.Tipo);

        // E ele NÃO segura a transação: +25 não alcança o limiar de revisão.
        // A demonstração precisa de um caso assim, senão quem vê o produto
        // conclui que todo sinal vira alerta — e passa a estranhar o dia em
        // que um não virar. O score é aditivo, e este teste é o que impede
        // alguém de "consertar" isso achando que é defeito.
        Assert.Equal(Decisao.Permitir, avaliacao.Decisao);
        Assert.True(avaliacao.Score > 0, "O sinal precisa aparecer no score, mesmo sem alarmar.");
    }

    [Fact]
    public async Task A_rajada_aciona_velocidade_e_deixa_um_alerta_esperando_na_fila()
    {
        var avaliacao = await AvaliacaoDeAsync("demo-c-maior-da-rajada");

        Assert.Contains(avaliacao.Sinais, s => s.Tipo == TipoDeRegra.VelocidadePorCliente);
        Assert.NotEqual(Decisao.Permitir, avaliacao.Decisao);

        // **A fila precisa ter trabalho.** Uma demonstração em que todo alerta
        // já está dentro de um caso não mostra a tela que o analista usa o dia
        // inteiro — e é o tipo de vazio que só aparece quando alguém abre o
        // produto, nunca quando se olha o seed.
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        var semDono = await contexto.Alertas
            .IgnoreQueryFilters()
            .CountAsync(a => a.CasoId == null && a.AvaliacaoId == avaliacao.Id, Cancelamento);

        Assert.Equal(1, semDono);
    }

    [Fact]
    public async Task Existe_um_falso_positivo_explicito_e_ele_esta_resolvido()
    {
        // CLAUDE.md seção 94: a demonstração PRECISA mostrar pelo menos um caso
        // em que o motor mandou revisar e a pessoa concluiu que era legítima.
        // É o que explica por que existe investigação humana no produto.
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        var caso = await contexto.Casos
            .IgnoreQueryFilters()
            .Where(c => c.Resultado == ResultadoDaInvestigacao.Legitima)
            .FirstOrDefaultAsync(Cancelamento);

        Assert.NotNull(caso);
        Assert.Equal(StatusDoCaso.Resolvido, caso.Status);
        Assert.NotNull(caso.ResponsavelId);

        // O alerta aponta para o caso, e nao o contrario: o caso e a raiz do
        // agregado e nao carrega colecao de alertas.
        var alertas = await contexto.Alertas
            .IgnoreQueryFilters()
            .Where(a => a.CasoId == caso.Id)
            .ToListAsync(Cancelamento);

        // A decisão automática NÃO foi reescrita. É a metade menos óbvia da
        // história, e a que o produto inteiro depende: o resultado humano
        // registra que o motor errou, sem apagar o que ele decidiu.
        var alerta = Assert.Single(alertas);
        var avaliacao = await contexto.AvaliacoesDeRisco
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Id == alerta.AvaliacaoId, Cancelamento);

        Assert.NotEqual(Decisao.Permitir, avaliacao.Decisao);
    }

    [Fact]
    public async Task Existe_um_caso_aberto_com_mais_de_um_alerta()
    {
        // História F: agrupamento e linha do tempo em andamento. Uma fila sem
        // trabalho por fazer não demonstra a tela de investigação.
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        var casos = await contexto.Casos
            .IgnoreQueryFilters()
            .Where(c => c.Status == StatusDoCaso.EmAnalise)
            .ToListAsync(Cancelamento);

        var porCaso = await contexto.Alertas
            .IgnoreQueryFilters()
            .Where(a => a.CasoId != null)
            .GroupBy(a => a.CasoId!.Value)
            .Select(g => new { CasoId = g.Key, Quantidade = g.Count() })
            .ToListAsync(Cancelamento);

        var comVarios = Assert.Single(
            casos,
            c => porCaso.Any(p => p.CasoId == c.Id && p.Quantidade > 1));

        // Abertura, atribuição, associação e nota: a linha do tempo precisa
        // ter o que mostrar quando alguém abrir o caso na demonstração.
        var eventos = await contexto.EventosDoCaso
            .IgnoreQueryFilters()
            .CountAsync(e => e.CasoId == comVarios.Id, Cancelamento);

        Assert.True(eventos >= 3, $"A linha do tempo tem {eventos} evento(s); esperado ao menos 3.");
    }

    [Fact]
    public async Task Rodar_o_seed_de_novo_nao_duplica_nada()
    {
        var antes = await ContarTransacoesDaDemoAsync();

        using (var escopo = _fabrica.Services.CreateScope())
        {
            await CentralAntifraude.Infrastructure.Persistencia.SeedDeDesenvolvimento.ExecutarAsync(
                escopo.ServiceProvider,
                _fabrica.Services.GetRequiredService<IConfiguration>(),
                Cancelamento);
        }

        // Idempotência (ROADMAP 13.3). O marcador é a própria primeira
        // transação da história A: um dado real não pode divergir de si mesmo,
        // e uma tabela de controle poderia.
        Assert.Equal(antes, await ContarTransacoesDaDemoAsync());
    }

    [Fact]
    public async Task Nenhum_dado_da_demonstracao_parece_real()
    {
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        var transacoes = await contexto.Transacoes
            .IgnoreQueryFilters()
            .Where(t => t.IdentificadorExterno.StartsWith("demo-"))
            .ToListAsync(Cancelamento);

        Assert.NotEmpty(transacoes);

        // CLAUDE.md seções 58 e 92: nada de PAN, nada de PII. A referência do
        // instrumento é sempre um token de demonstração, e o cliente é um
        // apelido — não um nome de pessoa com sobrenome e CPF.
        Assert.All(transacoes, t =>
        {
            Assert.StartsWith("pi_demo_", t.ReferenciaDoInstrumento, StringComparison.Ordinal);
            Assert.StartsWith("cli-", t.ClienteExternoId, StringComparison.Ordinal);
            Assert.DoesNotContain('@', t.ClienteExternoId);
        });
    }

    // -----------------------------------------------------------------------

    private async Task<AvaliacaoDeRisco> AvaliacaoDeAsync(string identificadorExterno)
    {
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        var transacao = await contexto.Transacoes
            .IgnoreQueryFilters()
            .FirstAsync(t => t.IdentificadorExterno == identificadorExterno, Cancelamento);

        return await contexto.AvaliacoesDeRisco
            .IgnoreQueryFilters()
            .Include(a => a.Sinais)
            .FirstAsync(a => a.TransacaoId == transacao.Id, Cancelamento);
    }

    private async Task<Dictionary<Decisao, int>> ContarDecisoesAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        var identificadores = await contexto.Transacoes
            .IgnoreQueryFilters()
            .Where(t => t.IdentificadorExterno.StartsWith("demo-"))
            .Select(t => t.Id)
            .ToListAsync(Cancelamento);

        var avaliacoes = await contexto.AvaliacoesDeRisco
            .IgnoreQueryFilters()
            .Where(a => identificadores.Contains(a.TransacaoId))
            .ToListAsync(Cancelamento);

        return avaliacoes
            .GroupBy(a => a.Decisao)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private async Task<int> ContarTransacoesDaDemoAsync()
    {
        await using var contexto = CenarioDeIdentidade.CriarContexto(_banco.StringDeConexao);

        return await contexto.Transacoes
            .IgnoreQueryFilters()
            .CountAsync(t => t.IdentificadorExterno.StartsWith("demo-"), Cancelamento);
    }
}
