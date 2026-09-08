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

    /// <summary>
    /// O template sem os comentários.
    ///
    /// Existe porque metade das asserções aqui é "esta palavra não aparece", e
    /// o template explica em prosa justamente as palavras que proíbe — a
    /// explicação de por que não há reserva de concorrência contém a palavra
    /// `ReservedConcurrentExecutions`. Sem esta separação, a única forma de
    /// passar no teste seria não documentar a decisão, que é o oposto do que
    /// se quer.
    /// </summary>
    private static string TemplateSemComentarios =>
        string.Join(
            '\n',
            Template
                .Split('\n')
                .Where(linha => !linha.TrimStart().StartsWith('#')));

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
        Assert.DoesNotContain("ConnectionStrings", TemplateSemComentarios, StringComparison.OrdinalIgnoreCase);
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
        Assert.DoesNotContain("AWS::SSM::Parameter", TemplateSemComentarios, StringComparison.Ordinal);
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
    /// Cada função pode ler o CAMINHO do SSM, além dos parâmetros nele.
    ///
    /// **Este teste nasceu de um 502 em produção.** A política tinha só
    /// `.../${Ambiente}/*`, que cobre os parâmetros mas não o caminho. O
    /// provedor de configuração lê tudo de uma vez com `GetParametersByPath`,
    /// e a AWS avalia essa chamada contra o ARN do caminho — `.../${Ambiente}`,
    /// sem a barra. Um ARN não cobre o outro.
    ///
    /// O resultado foi a pior combinação possível: template válido no lint,
    /// stack em CREATE_COMPLETE, e toda invocação morrendo com
    /// "is not authorized to perform: ssm:GetParametersByPath". Nada antes do
    /// deploy apontava para o defeito.
    /// </summary>
    [Fact]
    public void Cada_funcao_le_o_caminho_do_SSM_e_os_parametros_nele()
    {
        var funcoes = Regex.Count(
            TemplateSemComentarios, "AWS::Serverless::Function", RegexOptions.CultureInvariant);

        var comCuringa = Regex.Count(
            TemplateSemComentarios,
            @"ParameterName: !Sub 'portfolio/central-antifraude/\$\{Ambiente\}/\*'",
            RegexOptions.CultureInvariant);

        var semCuringa = Regex.Count(
            TemplateSemComentarios,
            @"ParameterName: !Sub 'portfolio/central-antifraude/\$\{Ambiente\}'",
            RegexOptions.CultureInvariant);

        Assert.Equal(funcoes, comCuringa);
        Assert.Equal(funcoes, semCuringa);
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
        Assert.DoesNotContain(proibido, TemplateSemComentarios, StringComparison.Ordinal);
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
    /// O CORS é declarado num lugar só, e esse lugar é a aplicação.
    ///
    /// **Aprendido em produção.** O template declarava CORS na Function URL
    /// *e* a aplicação declarava o dela. O preflight saía certo — a Function
    /// URL o responde sozinha, sem invocar a função —, mas toda resposta
    /// simples voltava com `Access-Control-Allow-Origin` duas vezes, e o
    /// navegador trata header CORS duplicado como falha.
    ///
    /// O sintoma era perverso: `curl` recebia 200, os health checks passavam, e
    /// só o navegador reprovava. Nenhum teste de servidor pegaria isso.
    ///
    /// A autoridade tem que ser uma. É a aplicação, porque ela já decide origem
    /// permitida e já verifica `Origin` como defesa de CSRF, com testes desde a
    /// Fase 11.
    /// </summary>
    [Fact]
    public void O_CORS_e_declarado_em_um_lugar_so()
    {
        Assert.DoesNotContain("Cors:", TemplateSemComentarios, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowOrigins", TemplateSemComentarios, StringComparison.Ordinal);

        // A origem continua entrando na aplicação por variável de ambiente — é
        // assim que ela sabe qual origem permitir.
        Assert.Contains(
            "Autenticacao__OrigensPermitidas__0: !Ref OrigemDoFrontend",
            TemplateSemComentarios,
            StringComparison.Ordinal);
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
    /// Nenhuma função reserva concorrência.
    ///
    /// **Esta asserção já foi o contrário, e o primeiro deploy real a inverteu.**
    /// A intenção era um teto por função, como contenção de acidente. O
    /// CloudFormation recusou: "Specified ReservedConcurrentExecutions for
    /// function decreases account's UnreservedConcurrentExecution below its
    /// minimum value of [10]".
    ///
    /// A conta tem limite de concorrência de 10, e não os 1.000 do padrão —
    /// contas novas começam baixo. Como a AWS exige deixar 10 não reservados,
    /// com um teto de 10 qualquer reserva é impossível. O limite da conta passa
    /// a ser o teto, e é global: mais apertado do que os tetos por função.
    ///
    /// O teste existe para que ninguém reintroduza a reserva "consertando" o
    /// template sem ter contexto — o deploy falharia de novo, com uma mensagem
    /// que não menciona o template.
    /// </summary>
    [Fact]
    public void Nenhuma_funcao_reserva_concorrencia()
    {
        Assert.DoesNotContain(
            "ReservedConcurrentExecutions", TemplateSemComentarios, StringComparison.Ordinal);
    }

    /// <summary>
    /// E ninguém provisiona concorrência.
    ///
    /// `ProvisionedConcurrency` seria a resposta óbvia para o arranque frio, e
    /// é justamente o que a meta de custo proíbe: ela cobra por hora, sempre,
    /// esteja a função sendo invocada ou não. É o único custo fixo que caberia
    /// por engano neste template (CLAUDE.md seção 76).
    /// </summary>
    [Fact]
    public void Nenhuma_funcao_provisiona_concorrencia()
    {
        Assert.DoesNotContain(
            "ProvisionedConcurrency", TemplateSemComentarios, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AutoPublishAlias", TemplateSemComentarios, StringComparison.Ordinal);
    }
}
