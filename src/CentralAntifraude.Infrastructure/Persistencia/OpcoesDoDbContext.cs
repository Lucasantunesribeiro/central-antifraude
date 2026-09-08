using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CentralAntifraude.Infrastructure.Persistencia;

/// <summary>
/// Montagem das opcoes do DbContext em um lugar so.
///
/// Existe porque tres consumidores precisam da MESMA configuracao e nao podem
/// divergir: a API, a fabrica de tempo de design (que gera as migrations) e
/// os testes de integracao contra PostgreSQL real. Se cada um montasse as
/// opcoes por conta propria, uma migration poderia ser gerada com convencao
/// de nomes diferente da que a aplicacao usa em execucao.
/// </summary>
public static class OpcoesDoDbContext
{
    /// <summary>Nome da string de conexao em IConfiguration.</summary>
    public const string NomeDaConexao = "Postgres";

    /// <summary>
    /// Quanto tempo uma conexao pode ficar ociosa no pool antes de ser
    /// descartada, em segundos.
    ///
    /// **Este numero foi aprendido em producao, e o defeito era feio.** O Neon
    /// suspende o compute apos 5 minutos de inatividade e fecha as conexoes.
    /// A Lambda, porem, continua quente por mais tempo que isso — e mantem o
    /// pool cheio de conexoes que o servidor ja encerrou. A invocacao seguinte
    /// pega uma delas e morre com `EndOfStreamException: Attempted to read past
    /// the end of the stream`.
    ///
    /// O sintoma e cruel numa demonstracao: a PRIMEIRA visita depois de um
    /// tempo parado recebe 500, e a segunda funciona — porque o pool ja
    /// descartou a conexao morta. Quem esta olhando conclui que o sistema e
    /// instavel.
    ///
    /// O padrao do Npgsql sao 300 segundos, exatamente o mesmo da suspensao do
    /// Neon: um empate em que o servidor quase sempre ganha. Sessenta segundos
    /// poda a conexao muito antes, ao custo de reabrir conexao com mais
    /// frequencia — o que num Lambda de baixo volume nao custa nada.
    ///
    /// **Por que nao `EnableRetryOnFailure`.** Seria a resposta obvia, e e a
    /// errada aqui por dois motivos. A estrategia de execucao do EF Core nao
    /// convive com transacoes iniciadas no codigo, e a operacao critica usa
    /// `SERIALIZABLE` explicito desde a Fase 4. E o CLAUDE.md secao 37 e direto:
    /// retry generico esconde a causa em vez de trata-la. Aqui a causa e
    /// conhecida — conexao ociosa que o servidor fechou — e tem correcao
    /// determinista.
    /// </summary>
    public const int SegundosDeVidaOciosaDaConexao = 60;

    public static void Configurar(
        DbContextOptionsBuilder construtor,
        string stringDeConexao)
    {
        ArgumentNullException.ThrowIfNull(construtor);
        ArgumentException.ThrowIfNullOrWhiteSpace(stringDeConexao);

        construtor
            .UseNpgsql(
                ComPodaDeConexaoOciosa(stringDeConexao),
                npgsql => npgsql.MigrationsAssembly(
                    typeof(CentralAntifraudeDbContext).Assembly.GetName().Name))
            // Nomes em snake_case sem aspas: um analista ou auditor abrindo o
            // psql durante uma investigacao consegue escrever a consulta sem
            // citar cada identificador. Convencao nativa do PostgreSQL.
            .UseSnakeCaseNamingConvention();
    }

    /// <summary>
    /// Aplica o tempo de vida ocioso, preservando um valor ja escolhido.
    ///
    /// Se a string de conexao ja trouxer `Connection Idle Lifetime`, quem a
    /// escreveu decidiu de proposito e a decisao vale — este metodo so preenche
    /// a ausencia. Sem essa deferencia, um ambiente com necessidade diferente
    /// nao teria como expressa-la.
    /// </summary>
    public static string ComPodaDeConexaoOciosa(string stringDeConexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stringDeConexao);

        // `DbConnectionStringBuilder`, e nao o do Npgsql, para descobrir o que
        // foi REALMENTE escrito: o builder tipado responde `ContainsKey` para
        // toda chave que ele conhece, tenha sido informada ou nao, entao com ele
        // nao ha como distinguir "escolheu 300" de "nao escolheu nada".
        var informadas = new DbConnectionStringBuilder { ConnectionString = stringDeConexao };

        var construtor = new NpgsqlConnectionStringBuilder(stringDeConexao);

        if (!informadas.ContainsKey("Connection Idle Lifetime") &&
            !informadas.ContainsKey("ConnectionIdleLifetime"))
        {
            construtor.ConnectionIdleLifetime = SegundosDeVidaOciosaDaConexao;
        }

        return construtor.ConnectionString;
    }
}
