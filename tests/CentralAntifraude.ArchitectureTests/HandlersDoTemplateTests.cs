using System.Reflection;
using System.Text.RegularExpressions;

namespace CentralAntifraude.ArchitectureTests;

/// <summary>
/// O template do CloudFormation aponta para código por STRING.
///
/// **É a única ligação do projeto inteiro que o compilador não confere.**
/// `CentralAntifraude.Lambdas::CentralAntifraude.Lambdas.ConsumidorHandler::TratarAsync`
/// é texto: renomear a classe, mover o namespace ou trocar o nome do método
/// deixa o build verde, os testes verdes e a função morta — e o defeito só
/// aparece na primeira invocação depois do deploy, como `ClassNotFound` no
/// CloudWatch, longe de quem fez a mudança.
///
/// Estes testes fecham essa ponta: cada handler declarado no template é
/// procurado por reflexão no assembly de verdade.
/// </summary>
public sealed class HandlersDoTemplateTests
{
    /// <summary>
    /// `ASSEMBLY::NAMESPACE.CLASSE::METODO` — o formato documentado da AWS
    /// para handler de biblioteca de classes. A API usa o outro formato, o de
    /// assembly executável, que é só o nome do assembly e não casa aqui.
    /// </summary>
    private static readonly Regex FormatoDoHandler = new(
        @"^\s*Handler:\s*(?<assembly>[\w.]+)::(?<tipo>[\w.]+)::(?<metodo>\w+)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static string Template =>
        File.ReadAllText(Path.Combine(RaizDoRepositorio.Caminho.FullName, "infra", "template.yaml"));

    public static TheoryData<string, string, string> HandlersDeclarados()
    {
        var dados = new TheoryData<string, string, string>();

        foreach (Match casamento in FormatoDoHandler.Matches(Template))
        {
            dados.Add(
                casamento.Groups["assembly"].Value,
                casamento.Groups["tipo"].Value,
                casamento.Groups["metodo"].Value);
        }

        return dados;
    }

    /// <summary>
    /// Guarda contra o template esvaziar sem ninguém notar: se o regex parasse
    /// de casar, a teoria acima rodaria zero vezes e passaria em silêncio.
    /// </summary>
    [Fact]
    public void O_template_declara_os_tres_workers()
    {
        Assert.Equal(3, FormatoDoHandler.Count(Template));
    }

    [Theory]
    [MemberData(nameof(HandlersDeclarados))]
    public void Cada_handler_do_template_existe_no_codigo(string assembly, string tipo, string metodo)
    {
        var montagem = Assembly.Load(assembly);

        var classe = montagem.GetType(tipo);
        Assert.True(classe is not null, $"O tipo '{tipo}' nao existe em '{assembly}'.");

        var alvo = classe!.GetMethod(metodo, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(alvo is not null, $"O metodo publico de instancia '{metodo}' nao existe em '{tipo}'.");

        // A classe precisa ter construtor sem parametros: a AWS a instancia
        // sozinha, e um construtor com dependencias falharia na inicializacao.
        Assert.True(
            classe.GetConstructor(Type.EmptyTypes) is not null,
            $"'{tipo}' precisa de um construtor sem parametros para a AWS instancia-lo.");
    }

    /// <summary>
    /// A API usa o outro modelo — assembly executável, com `AddAWSLambdaHosting`
    /// —, e ali o handler é só o nome do assembly. Trocar por engano um formato
    /// pelo outro é o erro mais fácil de cometer neste arquivo.
    /// </summary>
    [Fact]
    public void O_handler_da_api_e_o_nome_do_assembly_executavel()
    {
        // Ignora o fim de linha de propósito: o repositório é clonado em
        // Windows e em Linux, e uma asserção que depende de CRLF passa numa
        // máquina e falha na outra por um motivo que não é o do teste.
        Assert.Matches(
            new Regex(
                @"^\s*Handler:\s*CentralAntifraude\.Api\s*$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2)),
            Template);
    }

    /// <summary>
    /// Todo `CodeUri` aponta para uma pasta que existe e tem projeto dentro.
    /// Um caminho errado aqui só falharia no `sam build`, que não roda no CI de
    /// código.
    /// </summary>
    [Fact]
    public void Todo_CodeUri_aponta_para_um_projeto_existente()
    {
        var caminhos = Regex.Matches(
                Template,
                @"^\s*CodeUri:\s*(?<caminho>\S+)\s*$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2))
            .Select(m => m.Groups["caminho"].Value)
            .ToList();

        Assert.NotEmpty(caminhos);

        foreach (var caminho in caminhos)
        {
            var pasta = Path.GetFullPath(
                Path.Combine(RaizDoRepositorio.Caminho.FullName, "infra", caminho));

            Assert.True(Directory.Exists(pasta), $"CodeUri '{caminho}' aponta para pasta inexistente.");
            Assert.NotEmpty(Directory.GetFiles(pasta, "*.csproj"));
        }
    }
}
