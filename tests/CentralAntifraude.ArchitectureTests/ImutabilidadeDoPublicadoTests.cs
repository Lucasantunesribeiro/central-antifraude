using System.Reflection;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.ArchitectureTests;

/// <summary>
/// Vigia a imutabilidade do que ja foi publicado (CLAUDE.md secoes 23 e 24).
///
/// A promessa do produto e que uma avaliacao de marco continua explicavel pela
/// configuracao que valia em marco. Isso nao depende de disciplina: depende de
/// nao existir caminho para alterar uma versao publicada.
///
/// Hoje esse caminho nao existe. Este teste esta aqui para o dia em que
/// alguem, resolvendo um problema legitimo, adicionar um `AjustarPontos` em
/// <see cref="VersaoDeRegra"/> — e para que a build recuse antes que a
/// explicabilidade historica se perca em silencio.
///
/// **A verificacao nao e prova de pureza.** Um metodo que mutasse e devolvesse
/// algo passaria por ela. O que ela garante e que nao ha propriedade
/// gravavel de fora nem operacao de mutacao com a forma habitual — o suficiente
/// para que a mudanca seja deliberada, e nao acidental.
/// </summary>
public sealed class ImutabilidadeDoPublicadoTests
{
    public static TheoryData<Type> TiposPublicados() =>
        new(typeof(VersaoDeRegra), typeof(VersaoDePerfilDeRisco));

    [Theory]
    [MemberData(nameof(TiposPublicados))]
    public void Versao_publicada_nao_tem_propriedade_gravavel_de_fora(Type tipo)
    {
        // Os setters privados existem para o EF Core materializar a entidade.
        // Um setter publico seria uma porta aberta: bastaria carregar a versao
        // e atribuir.
        var gravaveis = tipo
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(propriedade => propriedade.SetMethod is { } setter && !setter.IsPrivate)
            .Select(propriedade => propriedade.Name)
            .ToList();

        Assert.True(
            gravaveis.Count == 0,
            $"{tipo.Name} tem propriedade gravavel de fora: {string.Join(", ", gravaveis)}. " +
            "Uma versao publicada nao muda.");
    }

    [Theory]
    [MemberData(nameof(TiposPublicados))]
    public void Versao_publicada_nao_tem_operacao_de_mutacao(Type tipo)
    {
        // Um metodo de instancia que nao devolve nada so pode existir para
        // mudar o proprio estado — e mudar o estado e exatamente o que uma
        // versao publicada nao faz.
        var mutacoes = tipo
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(metodo => !metodo.IsSpecialName)
            .Where(metodo => metodo.ReturnType == typeof(void))
            .Select(metodo => metodo.Name)
            .ToList();

        Assert.True(
            mutacoes.Count == 0,
            $"{tipo.Name} ganhou operacao de mutacao: {string.Join(", ", mutacoes)}. " +
            "Uma mudanca de regra ou de perfil cria uma versao nova, e nao altera a antiga.");
    }

    [Theory]
    [MemberData(nameof(TiposPublicados))]
    public void Versao_publicada_nao_expoe_campo_publico(Type tipo)
    {
        var campos = tipo
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(campo => campo.Name)
            .ToList();

        Assert.True(
            campos.Count == 0,
            $"{tipo.Name} expoe campo publico: {string.Join(", ", campos)}.");
    }

    [Fact]
    public void A_regra_e_mutavel_de_proposito()
    {
        // O contraponto do teste acima, e ele importa: se `Regra` tambem fosse
        // imutavel, nao haveria onde o rascunho morar, e a Fase 8 teria criado
        // uma tabela de rascunho so para contornar a propria decisao.
        //
        // A divisao e essa: a IDENTIDADE muda — nome, rascunho, ativacao; a
        // VERSAO publicada, nunca.
        var mutacoes = typeof(Regra)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(metodo => !metodo.IsSpecialName)
            .Select(metodo => metodo.Name)
            .ToList();

        Assert.Contains(nameof(Regra.SalvarRascunho), mutacoes);
        Assert.Contains(nameof(Regra.PublicarRascunho), mutacoes);
    }
}
