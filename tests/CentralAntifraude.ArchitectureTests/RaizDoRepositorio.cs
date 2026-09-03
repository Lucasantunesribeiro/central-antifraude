namespace CentralAntifraude.ArchitectureTests;

/// <summary>
/// Localiza a raiz do repositorio a partir do diretorio de execucao dos testes.
/// Necessario porque parte das regras e verificada lendo os arquivos .csproj,
/// nao apenas os assemblies compilados.
/// </summary>
public static class RaizDoRepositorio
{
    private const string ArquivoDaSolucao = "CentralAntifraude.slnx";

    public static DirectoryInfo Caminho { get; } = Localizar();

    public static string LerCsproj(string nomeDoProjeto)
    {
        var caminho = Path.Combine(
            Caminho.FullName,
            "src",
            nomeDoProjeto,
            $"{nomeDoProjeto}.csproj");

        if (!File.Exists(caminho))
        {
            throw new FileNotFoundException(
                $"Projeto '{nomeDoProjeto}' nao encontrado em {caminho}.", caminho);
        }

        return File.ReadAllText(caminho);
    }

    private static DirectoryInfo Localizar()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);

        while (diretorio is not null)
        {
            if (File.Exists(Path.Combine(diretorio.FullName, ArquivoDaSolucao)))
            {
                return diretorio;
            }

            diretorio = diretorio.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Nao foi possivel localizar '{ArquivoDaSolucao}' a partir de {AppContext.BaseDirectory}.");
    }
}
