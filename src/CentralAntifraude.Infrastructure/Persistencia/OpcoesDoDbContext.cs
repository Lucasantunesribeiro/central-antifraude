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
                AjustarConexaoParaAmbiente(stringDeConexao),
                npgsql => npgsql.MigrationsAssembly(
                    typeof(CentralAntifraudeDbContext).Assembly.GetName().Name))
            // Nomes em snake_case sem aspas: um analista ou auditor abrindo o
            // psql durante uma investigacao consegue escrever a consulta sem
            // citar cada identificador. Convencao nativa do PostgreSQL.
            .UseSnakeCaseNamingConvention();
    }

    /// <summary>
    /// Ajusta a string de conexao ao ambiente onde ela vai rodar.
    ///
    /// **Duas correcoes, e a segunda so apareceu em producao.**
    ///
    /// A primeira e a poda por tempo ocioso: sem ela, o padrao do Npgsql (300s)
    /// empata com a suspensao do Neon (300s), e o servidor quase sempre ganha.
    /// Preenchida so quando a string nao a traz — quem escreveu um valor
    /// proprio decidiu de proposito.
    ///
    /// A segunda e maior: **dentro de um Lambda, o POOL LOCAL e desligado.** O
    /// motivo e sutil. O Neon suspende o compute apos 5 min e fecha as conexoes;
    /// a poda por tempo ocioso deveria descartar a conexao morta, mas ela roda
    /// num timer de fundo — e o Lambda CONGELA o processo inteiro entre
    /// invocacoes, timer incluido. Quando a funcao descongela, o pool entrega
    /// uma conexao que morreu durante o congelamento, e a primeira query falha
    /// com `EndOfStreamException`. A segunda funciona, porque o pool ja
    /// descartou a morta — e um 500 na primeira visita depois de um tempo
    /// parado, que numa demonstracao e A visita.
    ///
    /// Sem pool local, cada `DbContext` abre a conexao na hora e a fecha ao
    /// terminar. Nao ha conexao guardada para morrer no congelamento. O pool de
    /// verdade passa a ser o PgBouncer do proprio Neon, do outro lado da
    /// conexao — que e onde o pool deve ficar num ambiente serverless de
    /// qualquer forma. Fora do Lambda (testes, migrations, execucao local) o
    /// pool continua ligado: ali o processo nao congela, e o pool vale a pena.
    /// </summary>
    public static string AjustarConexaoParaAmbiente(string stringDeConexao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stringDeConexao);

        // `DbConnectionStringBuilder`, e nao o do Npgsql, para descobrir o que
        // foi REALMENTE escrito: o builder tipado responde `ContainsKey` para
        // toda chave que ele conhece, tenha sido informada ou nao.
        var informadas = new DbConnectionStringBuilder { ConnectionString = stringDeConexao };

        var construtor = new NpgsqlConnectionStringBuilder(stringDeConexao);

        if (!informadas.ContainsKey("Connection Idle Lifetime") &&
            !informadas.ContainsKey("ConnectionIdleLifetime"))
        {
            construtor.ConnectionIdleLifetime = SegundosDeVidaOciosaDaConexao;
        }

        if (Configuracao.ConfiguracaoDaNuvem.DentroDoLambda &&
            !informadas.ContainsKey("Pooling"))
        {
            construtor.Pooling = false;
        }

        return construtor.ConnectionString;
    }
}
