using Amazon.Extensions.NETCore.Setup;
using Microsoft.Extensions.Configuration;

namespace CentralAntifraude.Infrastructure.Configuracao;

/// <summary>
/// De onde vem a configuração quando a aplicação roda na AWS.
///
/// **O problema.** A string de conexão do PostgreSQL é um segredo, e segredo
/// não entra no repositório, nem no template do CloudFormation, nem em variável
/// de ambiente do Lambda — a última é a mais traiçoeira das três, porque parece
/// segura e aparece em texto puro para qualquer pessoa que abra a configuração
/// da função no console (CLAUDE.md seção 59).
///
/// **A saída.** O SSM Parameter Store guarda o valor como `SecureString`, e a
/// função o lê no arranque com a permissão que o template concede — e só ela.
/// Rotacionar um segredo passa a ser trocar um parâmetro, e não republicar uma
/// pilha inteira.
///
/// **Por que parâmetros padrão, e não avançados.** Os padrões não têm custo de
/// armazenamento nem de chamada de API; os avançados custam US$ 0,05 por mês
/// por parâmetro, para sempre, e a meta de custo do projeto é zero. O limite de
/// 4 KB do padrão é folgado para uma string de conexão.
/// </summary>
public static class ConfiguracaoDaNuvem
{
    /// <summary>
    /// Prefixo dos parâmetros. Tudo abaixo dele vira chave de configuração:
    /// `/portfolio/central-antifraude/producao/ConnectionStrings/Postgres`
    /// chega ao aplicativo como `ConnectionStrings:Postgres`.
    ///
    /// **O `/portfolio` na frente não é enfeite.** A conta AWS hospeda mais de
    /// um projeto de portfólio, e o outro já usa `/portfolio/&lt;projeto&gt;/`.
    /// Dois padrões na mesma conta significam duas convenções para procurar,
    /// e uma política IAM que precisa listar as duas.
    /// </summary>
    public const string Prefixo = "/portfolio/central-antifraude";

    /// <summary>
    /// A variável que a AWS define em toda função Lambda, e ninguém mais.
    ///
    /// É o sinal usado para decidir se vale a pena procurar o Parameter Store.
    /// Um nome de ambiente — `Production` — não serviria: a suíte de integração
    /// roda como `Production` de propósito, e passaria a tentar falar com a AWS
    /// em toda execução, sem credencial, num timeout por teste.
    /// </summary>
    public const string MarcaDoLambda = "AWS_LAMBDA_FUNCTION_NAME";

    /// <summary>Estamos mesmo dentro de uma função Lambda?</summary>
    public static bool DentroDoLambda =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(MarcaDoLambda));

    /// <summary>
    /// Acrescenta os parâmetros do ambiente quando — e somente quando — a
    /// aplicação está mesmo dentro de um Lambda.
    /// </summary>
    public static IConfigurationBuilder AdicionarParametrosDaNuvem(
        this IConfigurationBuilder construtor,
        string ambiente)
    {
        ArgumentNullException.ThrowIfNull(construtor);
        ArgumentException.ThrowIfNullOrWhiteSpace(ambiente);

        if (!DentroDoLambda)
        {
            return construtor;
        }

        construtor.AddSystemsManager(configuracao =>
        {
            configuracao.Path = $"{Prefixo}/{ambiente}";

            // Falha fechada. Sem os parâmetros não há string de conexão, e a
            // função subiria para responder erro em toda avaliação de risco —
            // um processo saudável que não funciona é pior do que um que não
            // sobe, porque o alarme de saúde não dispara.
            configuracao.Optional = false;

            // Sem recarga periódica: cada consulta ao Parameter Store é uma
            // chamada de API, e uma função que só vive segundos não ganha nada
            // relendo o que leu no arranque. Trocar um segredo exige publicar
            // a função de novo, o que é explícito e auditável.
            configuracao.ReloadAfter = null;

            configuracao.AwsOptions = new AWSOptions();
        });

        return construtor;
    }
}
