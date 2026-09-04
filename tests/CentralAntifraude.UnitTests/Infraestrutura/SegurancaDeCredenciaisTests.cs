using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Infrastructure;
using CentralAntifraude.Infrastructure.Identidade;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CentralAntifraude.UnitTests.Infraestrutura;

public sealed class HashDeSenhaTests
{
    // Custo reduzido de proposito nos testes: 220.000 iteracoes por chamada
    // tornariam a suite lenta sem provar nada a mais sobre o comportamento.
    // O valor de producao e verificado separadamente, abaixo.
    private static readonly HashDeSenhaPbkdf2 Hash =
        new(Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));

    [Fact]
    public void Senha_correta_e_aceita()
    {
        var hash = Hash.Gerar("senha-de-teste-12345");

        Assert.Equal(
            ResultadoDaVerificacaoDeSenha.Valida,
            Hash.Verificar(hash, "senha-de-teste-12345"));
    }

    [Fact]
    public void Senha_errada_e_recusada()
    {
        var hash = Hash.Gerar("senha-de-teste-12345");

        Assert.Equal(
            ResultadoDaVerificacaoDeSenha.Invalida,
            Hash.Verificar(hash, "senha-de-teste-12346"));
    }

    [Fact]
    public void A_mesma_senha_gera_hashes_diferentes()
    {
        // Salt aleatorio por senha: sem ele, duas contas com a mesma senha
        // teriam o mesmo hash, e quebrar uma quebraria as duas.
        Assert.NotEqual(Hash.Gerar("mesma-senha-123"), Hash.Gerar("mesma-senha-123"));
    }

    [Fact]
    public void O_hash_nao_contem_a_senha()
    {
        var hash = Hash.Gerar("senha-secreta-do-usuario");

        Assert.DoesNotContain("senha-secreta", hash, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nao-e-base64-valido!!!")]
    [InlineData("AQAAAAIAAYag")]
    public void Hash_corrompido_no_banco_recusa_em_vez_de_explodir(string hashInvalido)
    {
        // Uma excecao aqui viraria 500 e, com isso, um oraculo: o atacante
        // saberia distinguir "conta com hash corrompido" de "senha errada".
        Assert.Equal(
            ResultadoDaVerificacaoDeSenha.Invalida,
            Hash.Verificar(hashInvalido, "qualquer-senha"));
    }

    [Fact]
    public void Hash_gerado_com_custo_menor_pede_regravacao()
    {
        // E assim que o custo sobe sem forcar ninguem a trocar de senha: no
        // proximo login, o hash antigo e detectado e regravado.
        var hashAntigo = new HashDeSenhaPbkdf2(
            Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }))
            .Gerar("senha-de-teste-12345");

        var hashAtual = new HashDeSenhaPbkdf2(
            Options.Create(new PasswordHasherOptions { IterationCount = 2_000 }));

        Assert.Equal(
            ResultadoDaVerificacaoDeSenha.ValidaMasPrecisaRegravar,
            hashAtual.Verificar(hashAntigo, "senha-de-teste-12345"));
    }

    [Fact]
    public void O_custo_configurado_no_projeto_segue_a_recomendacao_consultada()
    {
        // OWASP Password Storage Cheat Sheet, consultada em 2026-09-03:
        // PBKDF2-HMAC-SHA512 com 220.000 iteracoes. Este teste existe para que
        // baixar o custo seja uma decisao consciente, e nao um ajuste de
        // desempenho que ninguem percebeu.
        Assert.Equal(220_000, InjecaoDeDependencia.IteracoesDeHashDeSenha);
    }
}

public sealed class ProtetorDeRefreshTokenTests
{
    private static readonly ProtetorDeRefreshToken Protetor = new();

    [Fact]
    public void Cada_token_gerado_e_diferente()
    {
        var gerados = Enumerable.Range(0, 500).Select(_ => Protetor.Gerar().Bruto).ToList();

        Assert.Equal(gerados.Count, gerados.Distinct().Count());
    }

    [Fact]
    public void O_hash_devolvido_confere_com_o_valor_bruto()
    {
        var (bruto, hash) = Protetor.Gerar();

        Assert.Equal(hash, Protetor.CalcularHash(bruto));
    }

    [Fact]
    public void O_hash_nao_revela_o_token()
    {
        var (bruto, hash) = Protetor.Gerar();

        Assert.DoesNotContain(bruto, hash, StringComparison.Ordinal);
        // SHA-256 em hexadecimal: 64 caracteres, do tamanho da coluna.
        Assert.Equal(64, hash.Length);
        Assert.True(hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    [Fact]
    public void O_token_e_seguro_para_viajar_em_cookie()
    {
        // Base64url: sem "+", "/" ou "=", que exigiriam escape no cookie e
        // convidam a bug de codificacao entre servidor e navegador.
        var (bruto, _) = Protetor.Gerar();

        Assert.DoesNotContain('+', bruto);
        Assert.DoesNotContain('/', bruto);
        Assert.DoesNotContain('=', bruto);
        // 32 bytes em base64url sem preenchimento.
        Assert.Equal(43, bruto.Length);
    }
}

public sealed class OpcoesDeAutenticacaoTests
{
    private static OpcoesDeAutenticacao Validas() => new()
    {
        ChaveDeAssinatura = Convert.ToBase64String(new byte[48]),
    };

    [Fact]
    public void Configuracao_valida_passa()
    {
        Validas().Validar();
    }

    [Fact]
    public void Chave_ausente_derruba_a_inicializacao()
    {
        // Falha fechada: sem chave, a aplicacao nao sobe. O contrario seria
        // subir e recusar todo login com "token invalido", sem explicacao.
        var opcoes = Validas();
        opcoes.ChaveDeAssinatura = string.Empty;

        var erro = Assert.Throws<InvalidOperationException>(opcoes.Validar);
        Assert.Contains("ChaveDeAssinatura", erro.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Chave_curta_demais_e_recusada()
    {
        // HMAC-SHA256 exige chave de ao menos 256 bits (RFC 7518, secao 3.2).
        // Uma chave menor enfraquece a assinatura sem dar nenhum aviso.
        var opcoes = Validas();
        opcoes.ChaveDeAssinatura = Convert.ToBase64String(new byte[16]);

        Assert.Throws<InvalidOperationException>(opcoes.Validar);
    }

    [Fact]
    public void Chave_que_nao_e_base64_e_recusada()
    {
        var opcoes = Validas();
        opcoes.ChaveDeAssinatura = "isto nao e base64 !!!";

        Assert.Throws<InvalidOperationException>(opcoes.Validar);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(120)]
    public void Vida_do_access_token_fora_da_faixa_e_recusada(int minutos)
    {
        var opcoes = Validas();
        opcoes.MinutosDoAccessToken = minutos;

        Assert.Throws<InvalidOperationException>(opcoes.Validar);
    }

    [Fact]
    public void Vida_do_refresh_token_fora_da_faixa_e_recusada()
    {
        var opcoes = Validas();
        opcoes.DiasDoRefreshToken = 365;

        Assert.Throws<InvalidOperationException>(opcoes.Validar);
    }
}
