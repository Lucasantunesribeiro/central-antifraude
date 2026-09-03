using System.Reflection;
using System.Xml.Linq;
using CentralAntifraude.Application.Comum;
using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Infrastructure;

namespace CentralAntifraude.ArchitectureTests;

/// <summary>
/// Regras de dependencia entre camadas (CLAUDE.md secao 26, ROADMAP secao 0.6).
///
/// A verificacao acontece em dois niveis de proposito:
///
/// 1. no .csproj - pega uma referencia de projeto indevida mesmo que ninguem
///    a use ainda, ou seja, no momento em que ela e adicionada;
/// 2. no assembly compilado - pega o uso real, inclusive por caminho indireto.
///
/// So o segundo nivel deixaria passar meses uma referencia errada esperando o
/// primeiro `using`.
/// </summary>
public sealed class DependenciasEntreCamadasTests
{
    private const string Dominio = "CentralAntifraude.Domain";
    private const string Aplicacao = "CentralAntifraude.Application";
    private const string Infraestrutura = "CentralAntifraude.Infrastructure";
    private const string Api = "CentralAntifraude.Api";

    private static readonly Assembly AssemblyDoDominio = typeof(Identificador).Assembly;
    private static readonly Assembly AssemblyDaAplicacao = typeof(Pagina<>).Assembly;
    private static readonly Assembly AssemblyDaInfraestrutura = typeof(InjecaoDeDependencia).Assembly;
    private static readonly Assembly AssemblyDaApi = typeof(Program).Assembly;

    // -----------------------------------------------------------------------
    // Nivel 1: o que cada .csproj declara
    // -----------------------------------------------------------------------

    [Fact]
    public void Dominio_nao_referencia_nenhum_outro_projeto()
    {
        Assert.Empty(ReferenciasDeProjeto(Dominio));
    }

    [Fact]
    public void Dominio_nao_referencia_nenhum_pacote_de_terceiro()
    {
        // O nucleo do dominio precisa continuar compilavel e testavel sem
        // banco, sem web e sem serializador. E o que garante que uma regra
        // antifraude possa ser lida e verificada isoladamente.
        var pacotes = ReferenciasDePacote(Dominio);

        Assert.True(
            pacotes.Count == 0,
            $"O dominio passou a depender de: {string.Join(", ", pacotes)}.");
    }

    [Fact]
    public void Aplicacao_referencia_apenas_o_dominio()
    {
        Assert.Equal([Dominio], ReferenciasDeProjeto(Aplicacao));
    }

    [Fact]
    public void Infraestrutura_nao_referencia_a_api()
    {
        Assert.DoesNotContain(Api, ReferenciasDeProjeto(Infraestrutura));
    }

    [Fact]
    public void Api_nao_referencia_pacote_de_persistencia()
    {
        // A composicao passa por Infrastructure.AdicionarInfraestrutura.
        // Se EF Core ou Npgsql aparecerem no .csproj da API, a persistencia
        // comecou a vazar para a borda HTTP.
        var pacotes = ReferenciasDePacote(Api);

        Assert.DoesNotContain(pacotes, pacote =>
            pacote.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
            pacote.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // Nivel 2: o que cada assembly realmente usa
    // -----------------------------------------------------------------------

    [Fact]
    public void Assembly_do_dominio_nao_depende_de_nenhuma_camada_acima()
    {
        var proibidos = new[] { Aplicacao, Infraestrutura, Api };

        Assert.Empty(ReferenciasDeAssembly(AssemblyDoDominio).Intersect(proibidos, StringComparer.Ordinal));
    }

    [Fact]
    public void Assembly_do_dominio_so_depende_da_biblioteca_padrao()
    {
        var externas = ReferenciasDeAssembly(AssemblyDoDominio)
            .Where(nome => !EhBibliotecaPadrao(nome))
            .ToList();

        Assert.True(
            externas.Count == 0,
            $"O dominio passou a depender de: {string.Join(", ", externas)}.");
    }

    [Fact]
    public void Assembly_da_aplicacao_nao_depende_de_infraestrutura_nem_da_api()
    {
        var referencias = ReferenciasDeAssembly(AssemblyDaAplicacao);

        Assert.DoesNotContain(Infraestrutura, referencias);
        Assert.DoesNotContain(Api, referencias);
    }

    [Fact]
    public void Assembly_da_aplicacao_nao_conhece_ef_core_nem_o_driver_do_banco()
    {
        var persistencia = ReferenciasDeAssembly(AssemblyDaAplicacao)
            .Where(nome =>
                nome.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
                nome.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            persistencia.Count == 0,
            "Persistencia deve ficar confinada a Infrastructure. " +
            $"A camada de aplicacao passou a referenciar: {string.Join(", ", persistencia)}. " +
            "Se a decisao mudou, registre um ADR em docs/adr/ antes de remover esta regra.");
    }

    [Fact]
    public void Assembly_da_aplicacao_nao_conhece_aspnet()
    {
        var web = ReferenciasDeAssembly(AssemblyDaAplicacao)
            .Where(nome => nome.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToList();

        Assert.True(web.Count == 0, $"A aplicacao passou a depender de: {string.Join(", ", web)}.");
    }

    [Fact]
    public void Assembly_da_infraestrutura_nao_depende_da_api()
    {
        Assert.DoesNotContain(Api, ReferenciasDeAssembly(AssemblyDaInfraestrutura));
    }

    [Fact]
    public void Assembly_da_api_nao_usa_ef_core_nem_o_driver_do_banco()
    {
        var persistencia = ReferenciasDeAssembly(AssemblyDaApi)
            .Where(nome =>
                nome.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
                nome.StartsWith("Npgsql", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            persistencia.Count == 0,
            $"A API passou a usar diretamente: {string.Join(", ", persistencia)}.");
    }

    // -----------------------------------------------------------------------

    private static IReadOnlyList<string> ReferenciasDeProjeto(string projeto) =>
        ItensDoCsproj(projeto, "ProjectReference", "Include")
            .Select(caminho => Path.GetFileNameWithoutExtension(caminho.Replace('\\', '/')))
            .Where(nome => !string.IsNullOrEmpty(nome))
            .Order(StringComparer.Ordinal)
            .ToList()!;

    private static IReadOnlyList<string> ReferenciasDePacote(string projeto) =>
        ItensDoCsproj(projeto, "PackageReference", "Include")
            .Order(StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<string> ItensDoCsproj(string projeto, string elemento, string atributo) =>
        XDocument.Parse(RaizDoRepositorio.LerCsproj(projeto))
            .Descendants(elemento)
            .Select(item => item.Attribute(atributo)?.Value)
            .Where(valor => !string.IsNullOrWhiteSpace(valor))
            .Select(valor => valor!);

    private static IReadOnlyList<string> ReferenciasDeAssembly(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(nome => nome.Name!)
            .Where(nome => !string.IsNullOrEmpty(nome))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static bool EhBibliotecaPadrao(string nomeDoAssembly) =>
        nomeDoAssembly is "netstandard" or "mscorlib" ||
        nomeDoAssembly == "System" ||
        nomeDoAssembly.StartsWith("System.", StringComparison.Ordinal);
}
