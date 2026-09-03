using System.Runtime.CompilerServices;

namespace CentralAntifraude.IntegrationTests.Infra;

/// <summary>
/// Conserta, apenas dentro do processo de teste, uma forma de DOCKER_HOST que
/// o Testcontainers nao aceita no Windows.
///
/// O problema, verificado nesta maquina:
///   DOCKER_HOST=npipe:////./pipe/docker_engine  -> "The endpoint is not a npipe URI"
///   DOCKER_HOST=npipe://./pipe/docker_engine    -> funciona
///
/// A forma de quatro barras e a que a documentacao da Docker usa e a que o
/// `docker` de linha de comando aceita sem reclamar, entao ela aparece
/// naturalmente na variavel de usuario de quem instalou o Docker Desktop.
/// O cliente npipe usado pelo Testcontainers exige a forma de duas barras.
///
/// A correcao vive aqui, e nao nas variaveis de ambiente da maquina, porque
/// DOCKER_HOST e do desenvolvedor e outros projetos dele dependem dela. Em
/// Linux (o CI) a variavel nao existe e este codigo nao faz nada.
/// </summary>
internal static class NormalizacaoDoDockerHost
{
    private const string Variavel = "DOCKER_HOST";
    private const string FormaProblematica = "npipe:////";
    private const string FormaAceita = "npipe://";

    [ModuleInitializer]
    internal static void Aplicar()
    {
        var atual = Environment.GetEnvironmentVariable(Variavel);

        if (string.IsNullOrEmpty(atual) ||
            !atual.StartsWith(FormaProblematica, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var corrigida = string.Concat(FormaAceita, atual.AsSpan(FormaProblematica.Length));

        Environment.SetEnvironmentVariable(Variavel, corrigida);
    }
}
