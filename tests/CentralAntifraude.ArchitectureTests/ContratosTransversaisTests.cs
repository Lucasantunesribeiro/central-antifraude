using System.Reflection;
using System.Text.RegularExpressions;
using CentralAntifraude.Application.Comum;

namespace CentralAntifraude.ArchitectureTests;

/// <summary>
/// Guarda os contratos transversais congelados na Fase 0
/// (docs/adr/0004-contratos-transversais.md).
///
/// Sao regras que hoje quase nao tem o que verificar - o projeto ainda e
/// pequeno. Elas existem exatamente por isso: entram baratas agora e passam a
/// valer sozinhas quando as Fases 1 a 15 acrescentarem codigo.
/// </summary>
public sealed partial class ContratosTransversaisTests
{
    [Fact]
    public void Metodo_assincrono_publico_da_aplicacao_aceita_cancellation_token()
    {
        // Sem CancellationToken, uma consulta pesada continua ocupando conexao
        // do banco depois que o cliente HTTP ja desistiu. Numa Lambda isso
        // vira tempo cobrado sem ninguem esperando a resposta.
        var faltantes = typeof(Pagina<>).Assembly
            .GetExportedTypes()
            .SelectMany(tipo => tipo.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(metodo => !metodo.IsSpecialName)
            .Where(metodo => RetornaTarefa(metodo.ReturnType))
            .Where(metodo => !metodo.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken)))
            .Select(metodo => $"{metodo.DeclaringType!.FullName}.{metodo.Name}")
            .ToList();

        Assert.True(
            faltantes.Count == 0,
            "Metodos assincronos publicos sem CancellationToken: " +
            string.Join(", ", faltantes));
    }

    [Fact]
    public void A_leitura_do_relogio_real_acontece_em_um_unico_lugar()
    {
        // BannedSymbols.txt proibe DateTime.UtcNow em src/. A unica forma de
        // burlar isso e suprimir RS0030. Este teste vigia essas supressoes:
        // se aparecer uma terceira, alguem contornou a decisao de tempo ou de
        // identificador em vez de usar IRelogio / Identificador.
        var arquivosComSupressao = ArquivosFonteDeSrc()
            .Where(arquivo => File.ReadAllText(arquivo)
                .Contains("#pragma warning disable RS0030", StringComparison.Ordinal))
            .Select(arquivo => Path.GetFileName(arquivo))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Identificador.cs", "RelogioSistema.cs"], arquivosComSupressao);
    }

    [Fact]
    public void Nenhum_arquivo_de_configuracao_versionado_carrega_string_de_conexao()
    {
        // Security Gate 0: a configuracao versionada nao traz credencial.
        // A string de conexao entra por variavel de ambiente ou user-secrets.
        var appsettings = Directory
            .EnumerateFiles(
                Path.Combine(RaizDoRepositorio.Caminho.FullName, "src"),
                "appsettings*.json",
                SearchOption.AllDirectories)
            .ToList();

        Assert.NotEmpty(appsettings);

        foreach (var arquivo in appsettings)
        {
            var conteudo = File.ReadAllText(arquivo);

            Assert.False(
                PadraoDeCredencial().IsMatch(conteudo),
                $"{Path.GetFileName(arquivo)} contem algo parecido com credencial.");
        }
    }

    private static bool RetornaTarefa(Type tipoDeRetorno)
    {
        if (tipoDeRetorno == typeof(Task) || tipoDeRetorno == typeof(ValueTask))
        {
            return true;
        }

        if (!tipoDeRetorno.IsGenericType)
        {
            return false;
        }

        var definicao = tipoDeRetorno.GetGenericTypeDefinition();
        return definicao == typeof(Task<>)
            || definicao == typeof(ValueTask<>)
            || definicao == typeof(IAsyncEnumerable<>);
    }

    private static IEnumerable<string> ArquivosFonteDeSrc() =>
        Directory.EnumerateFiles(
                Path.Combine(RaizDoRepositorio.Caminho.FullName, "src"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(arquivo => !arquivo.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(arquivo => !arquivo.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex(
        "(password|senha|pwd|secret|api[_-]?key|connectionstring)\\s*[=:]",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex PadraoDeCredencial();
}
