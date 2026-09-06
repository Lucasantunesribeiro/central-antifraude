using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// O catalogo fechado, visto como a porta de entrada da configuracao.
///
/// **Isto e o que impede o produto de virar uma DSL** (CLAUDE.md secao 21). A
/// administracao nao recebe expressao, SQL nem script: recebe um tipo do
/// catalogo e numeros nomeados, e um <c>switch</c> fechado decide o que pode
/// existir.
///
/// O teste mais importante do arquivo e o ultimo: os limites que a tela usa
/// para montar o formulario tem que ser os MESMOS que a configuracao valida.
/// Duas listas de limites que saem de sincronia produzem uma tela que aceita o
/// que o dominio recusa — e o Supervisor descobre isso no botao de salvar.
/// </summary>
public class CatalogoDeTiposDeRegraTests
{
    public static TheoryData<TipoDeRegra> TodosOsTipos()
    {
        var dados = new TheoryData<TipoDeRegra>();

        foreach (var tipo in Enum.GetValues<TipoDeRegra>())
        {
            dados.Add(tipo);
        }

        return dados;
    }

    [Theory]
    [MemberData(nameof(TodosOsTipos))]
    public void Todo_tipo_do_enum_tem_descricao(TipoDeRegra tipo)
    {
        // Um tipo sem descricao ficaria invisivel na tela de criacao: existiria
        // no motor e ninguem conseguiria configura-lo.
        var descricao = CatalogoDeTiposDeRegra.Descrever(tipo);

        Assert.NotEmpty(descricao.Rotulo);
        Assert.NotEmpty(descricao.Resumo);
        Assert.NotEmpty(descricao.Campos);
    }

    [Theory]
    [MemberData(nameof(TodosOsTipos))]
    public void Os_valores_padrao_de_todo_tipo_montam_uma_configuracao_valida(TipoDeRegra tipo)
    {
        // O padrao e o que o formulario mostra ao abrir. Se ele nao passasse
        // pela validacao, a tela nasceria com erro antes de a pessoa digitar.
        var descricao = CatalogoDeTiposDeRegra.Descrever(tipo);
        var padroes = descricao.Campos.ToDictionary(c => c.Nome, c => c.Padrao, StringComparer.Ordinal);

        Assert.True(
            CatalogoDeTiposDeRegra.TentarMontar(tipo, padroes, out var configuracao, out var erro),
            erro);

        Assert.NotNull(configuracao);
        Assert.Equal(tipo, configuracao.Tipo);
    }

    [Theory]
    [MemberData(nameof(TodosOsTipos))]
    public void Montar_e_desmontar_devolve_os_mesmos_numeros(TipoDeRegra tipo)
    {
        // A volta existe para abrir um rascunho no formulario com os valores
        // preenchidos. Se ela perdesse um campo, editar um rascunho apagaria
        // silenciosamente aquele numero.
        var descricao = CatalogoDeTiposDeRegra.Descrever(tipo);
        var padroes = descricao.Campos.ToDictionary(c => c.Nome, c => c.Padrao, StringComparer.Ordinal);

        CatalogoDeTiposDeRegra.TentarMontar(tipo, padroes, out var configuracao, out _);

        var volta = CatalogoDeTiposDeRegra.Desmontar(configuracao!);

        Assert.Equal(padroes.Count, volta.Count);

        foreach (var (nome, valor) in padroes)
        {
            Assert.Equal(valor, volta[nome]);
        }
    }

    [Fact]
    public void Campo_desconhecido_e_recusado_em_vez_de_ignorado()
    {
        // Ignorar faria quem enviou acreditar que o numero foi usado pelo
        // motor — e o comportamento real seria outro, sem aviso nenhum.
        var valores = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["maximoDeTransacoes"] = 3,
            ["janelaEmMinutos"] = 10,
            ["pontos"] = 999,
        };

        Assert.False(CatalogoDeTiposDeRegra.TentarMontar(
            TipoDeRegra.VelocidadePorCliente,
            valores,
            out var configuracao,
            out var erro));

        Assert.Null(configuracao);
        Assert.Contains("pontos", erro, StringComparison.Ordinal);
    }

    [Fact]
    public void Campo_faltando_e_recusado_em_vez_de_receber_um_padrao()
    {
        // Um padrao escolhido pelo servidor mudaria o comportamento do motor
        // sem ninguem ter decidido aquilo.
        var valores = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["maximoDeTransacoes"] = 3,
        };

        Assert.False(CatalogoDeTiposDeRegra.TentarMontar(
            TipoDeRegra.VelocidadePorCliente,
            valores,
            out _,
            out var erro));

        Assert.Contains("janelaEmMinutos", erro, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuracao_vazia_e_recusada()
    {
        Assert.False(CatalogoDeTiposDeRegra.TentarMontar(
            TipoDeRegra.NovoDispositivo,
            valores: null,
            out _,
            out var erro));

        Assert.NotEmpty(erro);
    }

    [Fact]
    public void Numero_fracionario_em_campo_inteiro_e_recusado()
    {
        // "3,5 tentativas em 10 minutos" nao significa nada. Truncar em
        // silencio faria a regra publicada divergir do que foi digitado.
        var valores = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["maximoDeTransacoes"] = 3.5m,
            ["janelaEmMinutos"] = 10,
        };

        Assert.False(CatalogoDeTiposDeRegra.TentarMontar(
            TipoDeRegra.VelocidadePorCliente,
            valores,
            out _,
            out var erro));

        Assert.Contains("inteiro", erro, StringComparison.Ordinal);
    }

    [Fact]
    public void Tipo_fora_do_catalogo_e_recusado()
    {
        // Um numero qualquer convertido para o enum nao vira regra: o
        // catalogo e fechado, e o desconhecido falha alto.
        Assert.False(CatalogoDeTiposDeRegra.TentarMontar(
            (TipoDeRegra)999,
            new Dictionary<string, decimal>(StringComparer.Ordinal),
            out _,
            out var erro));

        Assert.NotEmpty(erro);
    }

    [Fact]
    public void Campo_fracionario_aceita_casa_decimal()
    {
        var valores = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["multiploDaMedia"] = 2.5m,
            ["minimoDeTransacoesNoHistorico"] = 4,
        };

        Assert.True(CatalogoDeTiposDeRegra.TentarMontar(
            TipoDeRegra.ValorAcimaDoHistorico,
            valores,
            out var configuracao,
            out var erro),
            erro);

        var esperada = new ConfiguracaoDeValorAcimaDoHistorico(2.5m, 4);

        Assert.Equal(esperada, configuracao);
    }

    // -----------------------------------------------------------------------
    // O teste que amarra as duas listas de limites
    // -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(TodosOsTipos))]
    public void Os_limites_declarados_sao_os_mesmos_que_a_configuracao_valida(TipoDeRegra tipo)
    {
        // A faixa aparece em dois lugares: aqui, para a tela montar o
        // formulario, e dentro de `ConfiguracaoDeRegra.Validar`, que e a
        // autoridade. Este teste transforma a duplicacao em contrato
        // verificado: para cada campo, um passo alem de cada extremo tem que
        // ser recusado, e cada extremo tem que ser aceito.
        var descricao = CatalogoDeTiposDeRegra.Descrever(tipo);
        var padroes = descricao.Campos.ToDictionary(c => c.Nome, c => c.Padrao, StringComparer.Ordinal);

        foreach (var campo in descricao.Campos)
        {
            var passo = campo.Tipo == TipoDoCampo.Inteiro ? 1m : 0.1m;

            AssertAceito(tipo, padroes, campo.Nome, campo.Minimo);
            AssertAceito(tipo, padroes, campo.Nome, campo.Maximo);

            AssertRecusado(tipo, padroes, campo.Nome, campo.Minimo - passo);
            AssertRecusado(tipo, padroes, campo.Nome, campo.Maximo + passo);
        }
    }

    private static Dictionary<string, decimal> Com(
        IReadOnlyDictionary<string, decimal> padroes,
        string campo,
        decimal valor)
    {
        var copia = new Dictionary<string, decimal>(padroes, StringComparer.Ordinal)
        {
            [campo] = valor,
        };

        return copia;
    }

    private static void AssertAceito(
        TipoDeRegra tipo,
        IReadOnlyDictionary<string, decimal> padroes,
        string campo,
        decimal valor)
    {
        Assert.True(
            CatalogoDeTiposDeRegra.TentarMontar(tipo, Com(padroes, campo, valor), out _, out var erro),
            $"{tipo}.{campo} = {valor} deveria ser aceito: {erro}");
    }

    private static void AssertRecusado(
        TipoDeRegra tipo,
        IReadOnlyDictionary<string, decimal> padroes,
        string campo,
        decimal valor)
    {
        Assert.False(
            CatalogoDeTiposDeRegra.TentarMontar(tipo, Com(padroes, campo, valor), out _, out _),
            $"{tipo}.{campo} = {valor} deveria ser recusado.");
    }

    [Fact]
    public void Descrever_um_tipo_inexistente_falha_alto()
    {
        // Falhar alto, e nao devolver um catalogo vazio: um tipo que o
        // dominio nao conhece nao pode virar formulario nenhum.
        Assert.Throws<ViolacaoDeInvariante>(() => CatalogoDeTiposDeRegra.Descrever((TipoDeRegra)999));
    }
}
