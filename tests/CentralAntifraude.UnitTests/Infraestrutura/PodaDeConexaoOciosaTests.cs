using CentralAntifraude.Infrastructure.Persistencia;
using Npgsql;

namespace CentralAntifraude.UnitTests.Infraestrutura;

/// <summary>
/// O tempo de vida ocioso da conexão, que existe por causa de um 500 em
/// produção.
///
/// **O defeito.** O Neon suspende o compute após 5 minutos parado e fecha as
/// conexões. A Lambda continua quente por mais tempo que isso, com o pool cheio
/// de conexões que o servidor já encerrou. A invocação seguinte pega uma delas e
/// morre com `EndOfStreamException: Attempted to read past the end of the
/// stream`.
///
/// **Por que é pior do que parece.** A primeira visita depois de um tempo parado
/// recebe 500; a segunda funciona, porque o pool já descartou a conexão morta.
/// Numa demonstração de portfólio, a primeira visita é *a* visita — e o sistema
/// parece instável para quem só vai olhar uma vez.
///
/// O padrão do Npgsql são 300 segundos, exatamente o mesmo prazo da suspensão do
/// Neon. Um empate que o servidor quase sempre ganha.
/// </summary>
public sealed class PodaDeConexaoOciosaTests
{
    private const string Base =
        "Host=localhost;Port=5432;Database=x;Username=x;Password=x";

    /// <summary>
    /// Sem valor na string, o padrão seguro entra.
    /// </summary>
    [Fact]
    public void Aplica_o_tempo_de_vida_ocioso_quando_ele_nao_foi_escolhido()
    {
        var resultado = new NpgsqlConnectionStringBuilder(
            OpcoesDoDbContext.ComPodaDeConexaoOciosa(Base));

        Assert.Equal(
            OpcoesDoDbContext.SegundosDeVidaOciosaDaConexao,
            resultado.ConnectionIdleLifetime);
    }

    /// <summary>
    /// **A asserção que dá sentido ao número.** Ele precisa ser menor que os 300
    /// segundos que o Neon leva para suspender — senão a corrida continua, e o
    /// servidor ganha. Escrito como comparação, e não como igualdade a 60, para
    /// que ajustar o valor continue permitido e reduzi-lo abaixo do necessário
    /// não passe despercebido.
    /// </summary>
    [Fact]
    public void O_tempo_e_menor_que_a_suspensao_do_Neon()
    {
        const int suspensaoDoNeonEmSegundos = 300;

        Assert.True(
            OpcoesDoDbContext.SegundosDeVidaOciosaDaConexao < suspensaoDoNeonEmSegundos,
            "A conexão precisa ser podada ANTES de o Neon fechá-la, senão o pool " +
            "guarda conexões mortas e a primeira requisição depois da suspensão " +
            "falha com EndOfStreamException.");

        // E não pode ser tão curto a ponto de reabrir conexão a cada requisição:
        // no caminho crítico isso somaria handshake TLS a toda avaliação.
        Assert.True(OpcoesDoDbContext.SegundosDeVidaOciosaDaConexao >= 15);
    }

    /// <summary>
    /// Uma escolha explícita na string de conexão prevalece.
    ///
    /// Um ambiente com banco que não suspende — o PostgreSQL local dos testes,
    /// por exemplo — pode querer outro valor, e precisa ter como dizer isso.
    /// </summary>
    [Fact]
    public void Respeita_o_valor_que_ja_estava_na_string_de_conexao()
    {
        var resultado = new NpgsqlConnectionStringBuilder(
            OpcoesDoDbContext.ComPodaDeConexaoOciosa(
                Base + ";Connection Idle Lifetime=17"));

        Assert.Equal(17, resultado.ConnectionIdleLifetime);
    }

    /// <summary>
    /// O resto da string sobrevive à passagem — inclusive o SSL, que o Neon
    /// exige. Perder isso transformaria a correção de um defeito na criação de
    /// outro, e bem pior.
    /// </summary>
    [Fact]
    public void Preserva_os_demais_parametros()
    {
        var resultado = new NpgsqlConnectionStringBuilder(
            OpcoesDoDbContext.ComPodaDeConexaoOciosa(
                Base + ";SSL Mode=Require;Maximum Pool Size=3"));

        Assert.Equal("localhost", resultado.Host);
        Assert.Equal("x", resultado.Database);
        Assert.Equal(SslMode.Require, resultado.SslMode);
        Assert.Equal(3, resultado.MaxPoolSize);
    }
}
