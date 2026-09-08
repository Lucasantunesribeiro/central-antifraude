using System.Text.RegularExpressions;

namespace CentralAntifraude.ArchitectureTests;

/// <summary>
/// Security Gate da Fase 14, verificado no arquivo e não numa lista em prosa.
///
/// **Por que aqui, e não em `IntegrationTests` como os gates anteriores.** Os
/// outros gates exercitam a aplicação no ar — autenticação, isolamento de
/// tenant, autorização — e precisam de banco. Este verifica propriedades de um
/// arquivo de infraestrutura, e um gate que exige subir PostgreSQL para ler um
/// YAML é um gate que alguém vai pular quando estiver com pressa.
///
/// **O que ele protege.** Um template de infraestrutura é o lugar onde um erro
/// de segurança fica invisível: um recurso com curinga parece igual a um
/// recurso com ARN específico para quem passa os olhos, e a diferença é entre
/// "pode ler os próprios segredos" e "pode ler os de todo mundo".
/// </summary>
public sealed class SecurityGate14Tests
{
    private static string Template =>
        File.ReadAllText(Path.Combine(RaizDoRepositorio.Caminho.FullName, "infra", "template.yaml"));

    private static IEnumerable<string> ArquivosDeInfra() =>
        Directory.EnumerateFiles(
            Path.Combine(RaizDoRepositorio.Caminho.FullName, "infra"),
            "*",
            SearchOption.AllDirectories);

    /// <summary>
    /// Nenhum segredo nos arquivos de infraestrutura.
    ///
    /// A verificação é por PALAVRA, e não por valor: valores de segredo variam,
    /// mas a chave que os carrega quase sempre tem um destes nomes. Um falso
    /// positivo aqui custa uma renomeação; um falso negativo custa uma
    /// credencial publicada num repositório (CLAUDE.md seção 59).
    /// </summary>
    [Theory]
    [InlineData("password")]
    [InlineData("senha")]
    [InlineData("AKIA")]
    [InlineData("BEGIN PRIVATE KEY")]
    [InlineData("BEGIN RSA")]
    public void O_template_nao_carrega_segredo(string proibido)
    {
        foreach (var arquivo in ArquivosDeInfra())
        {
            Assert.DoesNotContain(
                proibido,
                File.ReadAllText(arquivo),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A string de conexão NÃO chega por variável de ambiente.
    ///
    /// É a opção mais fácil e a mais traiçoeira: parece segura e aparece em
    /// texto puro para qualquer pessoa que abra a configuração da função no
    /// console. O caminho correto é o Parameter Store como SecureString,
    /// criado fora deste arquivo.
    /// </summary>
    [Fact]
    public void A_string_de_conexao_nao_vem_por_variavel_de_ambiente()
    {
        Assert.DoesNotContain("ConnectionStrings", Template, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// O template não cria parâmetro nenhum no SSM.
    ///
    /// Um recurso de parâmetro aqui teria que carregar o valor junto — e o
    /// valor é o segredo. A ausência do recurso é a garantia.
    /// </summary>
    [Fact]
    public void O_template_nao_declara_parametro_no_SSM()
    {
        Assert.DoesNotContain("AWS::SSM::Parameter", Template, StringComparison.Ordinal);
    }

    /// <summary>
    /// Toda leitura de parâmetro é limitada ao caminho do ambiente.
    ///
    /// O caminho com o nome do ambiente dá acesso aos segredos daquele
    /// ambiente. Um curinga solto daria acesso aos de todos — inclusive os de
    /// produção a partir de homologação.
    /// </summary>
    [Fact]
    public void A_leitura_do_SSM_e_limitada_ao_caminho_do_ambiente()
    {
        var politicas = Regex.Matches(
            Template,
            @"SSMParameterReadPolicy:\s*\r?\n\s*ParameterName:\s*(?<valor>.+)",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));

        Assert.NotEmpty(politicas);

        foreach (Match politica in politicas)
        {
            var valor = politica.Groups["valor"].Value.Trim();

            Assert.Contains("${Ambiente}", valor, StringComparison.Ordinal);
            Assert.StartsWith("!Sub", valor, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Nenhuma política com curinga em ação ou recurso.
    ///
    /// O SAM gera IAM a partir de políticas nomeadas justamente para evitar
    /// isso. Uma política crua com ação curinga no meio do arquivo desfaria
    /// toda a contenção sem alterar uma linha de código.
    /// </summary>
    [Theory]
    [InlineData("Action: ")]
    [InlineData("Resource: ")]
    [InlineData("iam:")]
    [InlineData("AdministratorAccess")]
    [InlineData("PolicyDocument")]
    public void Nenhuma_politica_crua_e_declarada(string proibido)
    {
        Assert.DoesNotContain(proibido, Template, StringComparison.Ordinal);
    }

    /// <summary>
    /// Só a API é pública, e apenas ela tem Function URL.
    ///
    /// Autenticação NONE é correta para a API — a autenticação é da aplicação,
    /// e foi construída nas fases 1 e 11. Numa função de worker seria um
    /// endpoint aberto para invocar processamento de fila sem credencial.
    /// </summary>
    [Fact]
    public void Apenas_a_api_e_alcancavel_pela_internet()
    {
        Assert.Equal(1, Regex.Count(Template, "FunctionUrlConfig", RegexOptions.CultureInvariant));
        Assert.Equal(1, Regex.Count(Template, "AuthType: NONE", RegexOptions.CultureInvariant));
    }

    /// <summary>
    /// O CORS da Function URL não abre para qualquer origem.
    ///
    /// Com credenciais permitidas, uma origem curinga deixaria qualquer site do
    /// mundo fazer requisição autenticada em nome do usuário logado.
    /// </summary>
    [Fact]
    public void O_CORS_nao_aceita_qualquer_origem()
    {
        Assert.Contains("AllowOrigins: [!Ref OrigemDoFrontend]", Template, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowOrigins: [*", Template, StringComparison.Ordinal);
    }

    /// <summary>
    /// Todo grupo de log tem retenção declarada.
    ///
    /// O padrão do CloudWatch é "nunca expirar". Além da despesa, log guardado
    /// para sempre é dado pessoal guardado para sempre — e o produto registra
    /// fingerprint de dispositivo e derivados de IP.
    /// </summary>
    [Fact]
    public void Todo_grupo_de_log_tem_retencao_declarada()
    {
        var grupos = Regex.Count(Template, "AWS::Logs::LogGroup", RegexOptions.CultureInvariant);
        var retencoes = Regex.Count(Template, "RetentionInDays", RegexOptions.CultureInvariant);

        Assert.True(grupos > 0);
        Assert.Equal(grupos, retencoes);
    }

    /// <summary>
    /// Toda fila de trabalho tem para onde mandar o que falha repetidamente.
    ///
    /// Sem redrive, uma mensagem envenenada volta para sempre — e cada volta
    /// acorda o banco e gasta invocação (CLAUDE.md seção 72).
    /// </summary>
    [Fact]
    public void As_filas_de_trabalho_tem_redrive()
    {
        // Quatro filas: duas de trabalho e as duas de mortas correspondentes.
        // Só as de trabalho têm redrive; uma fila de mortas com redrive
        // apontando para outra seria um encadeamento sem fim.
        Assert.Equal(4, Regex.Count(Template, "AWS::SQS::Queue", RegexOptions.CultureInvariant));
        Assert.Equal(2, Regex.Count(Template, "RedrivePolicy", RegexOptions.CultureInvariant));
    }

    /// <summary>
    /// Toda função tem teto de concorrência.
    ///
    /// Não é economia: é contenção de acidente. Sem teto, um laço de retry mal
    /// resolvido consome a cota mensal — e o orçamento — numa tarde.
    /// </summary>
    [Fact]
    public void Toda_funcao_tem_teto_de_concorrencia()
    {
        var funcoes = Regex.Count(Template, "AWS::Serverless::Function", RegexOptions.CultureInvariant);
        var tetos = Regex.Count(Template, "ReservedConcurrentExecutions", RegexOptions.CultureInvariant);

        Assert.True(funcoes > 0);
        Assert.Equal(funcoes, tetos);
    }
}
