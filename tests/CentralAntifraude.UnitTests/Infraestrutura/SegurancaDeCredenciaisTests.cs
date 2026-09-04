using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Infrastructure;
using CentralAntifraude.Infrastructure.Identidade;
using CentralAntifraude.Infrastructure.Integracoes;
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

public sealed class ProtetorDeCredencialTests
{
    private static readonly ProtetorDeCredencial Protetor = new();

    [Fact]
    public void Toda_chave_gerada_e_interpretavel()
    {
        // ESTE teste existe por causa de um defeito real: o segredo e
        // base64url, alfabeto que inclui "_" - o mesmo caractere que separa
        // as partes da chave. Um Split sem limite quebrava a chave em quatro
        // pedacos e a recusava.
        //
        // Como so cerca de metade das chaves sorteadas contem "_", o defeito
        // aparecia em metade das execucoes. Gerar muitas chaves e conferir
        // TODAS transforma esse acaso em certeza.
        for (var i = 0; i < 500; i++)
        {
            var (identificadorPublico, hashDoSegredo, valorBruto) = Protetor.Gerar();

            Assert.True(
                Protetor.TentarInterpretar(valorBruto, out var lidoPublico, out var lidoHash),
                $"Chave gerada nao pode ser interpretada: {valorBruto}");

            Assert.Equal(identificadorPublico, lidoPublico);
            Assert.Equal(hashDoSegredo, lidoHash);
        }
    }

    [Fact]
    public void Interpreta_chave_cujo_segredo_contem_o_separador()
    {
        // O caso especifico, escrito na mao para nao depender de sorteio.
        const string comSeparador = "caf_0123456789abcdef_aa_bb_cc-dd";

        Assert.True(Protetor.TentarInterpretar(comSeparador, out var publico, out _));
        Assert.Equal("0123456789abcdef", publico);
    }

    [Fact]
    public void A_chave_tem_o_formato_documentado()
    {
        var (identificadorPublico, _, valorBruto) = Protetor.Gerar();

        // O prefixo fixo permite que um varredor de segredos reconheca a
        // chave se ela vazar num repositorio.
        Assert.StartsWith("caf_", valorBruto, StringComparison.Ordinal);
        Assert.Equal(16, identificadorPublico.Length);
        Assert.True(identificadorPublico.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'));
    }

    [Fact]
    public void Cada_chave_gerada_e_unica()
    {
        var geradas = Enumerable.Range(0, 500).Select(_ => Protetor.Gerar().ValorBruto).ToList();

        Assert.Equal(geradas.Count, geradas.Distinct().Count());
    }

    [Fact]
    public void O_hash_nao_revela_o_segredo()
    {
        var (_, hashDoSegredo, valorBruto) = Protetor.Gerar();
        var segredo = valorBruto.Split('_', 3)[2];

        Assert.DoesNotContain(segredo, hashDoSegredo, StringComparison.Ordinal);
        Assert.Equal(64, hashDoSegredo.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("caf_0123456789abcdef")]              // sem segredo
    [InlineData("caf__segredo")]                       // sem identificador
    [InlineData("xyz_0123456789abcdef_segredo")]       // prefixo errado
    [InlineData("caf_0123456789ABCDEF_segredo")]       // hexadecimal em maiusculas
    [InlineData("caf_curto_segredo")]                  // identificador curto
    [InlineData("caf_zzzzzzzzzzzzzzzz_segredo")]       // fora do alfabeto hexadecimal
    [InlineData("Bearer eyJhbGciOi")]                  // token humano
    public void Recusa_chave_malformada(string? valor)
    {
        Assert.False(Protetor.TentarInterpretar(valor, out _, out _));
    }

    [Fact]
    public void Segredos_diferentes_produzem_hashes_diferentes()
    {
        Protetor.TentarInterpretar("caf_0123456789abcdef_segredo-a", out _, out var hashA);
        Protetor.TentarInterpretar("caf_0123456789abcdef_segredo-b", out _, out var hashB);

        Assert.NotEqual(hashA, hashB);
    }
}

public sealed class FingerprintDeIpTests
{
    private static FingerprintDeIpComHmac Criar(byte semente = 1)
    {
        var chave = new byte[48];
        Array.Fill(chave, semente);

        return new FingerprintDeIpComHmac(
            new CentralAntifraude.Application.Transacoes.OpcoesDeIngestao
            {
                ChaveDeFingerprint = Convert.ToBase64String(chave),
            });
    }

    [Fact]
    public void O_mesmo_ip_produz_o_mesmo_fingerprint()
    {
        using var derivador = Criar();

        // E o que uma regra de risco precisa saber: "vieram do mesmo lugar?".
        Assert.Equal(derivador.Derivar("203.0.113.10"), derivador.Derivar("203.0.113.10"));
    }

    [Fact]
    public void Espacos_e_caixa_nao_mudam_o_fingerprint()
    {
        using var derivador = Criar();

        Assert.Equal(derivador.Derivar("2001:DB8::1"), derivador.Derivar(" 2001:db8::1 "));
    }

    [Fact]
    public void Ips_diferentes_produzem_fingerprints_diferentes()
    {
        using var derivador = Criar();

        Assert.NotEqual(derivador.Derivar("203.0.113.10"), derivador.Derivar("203.0.113.11"));
    }

    [Fact]
    public void O_fingerprint_nao_contem_o_endereco()
    {
        using var derivador = Criar();

        var fingerprint = derivador.Derivar("203.0.113.10")!;

        Assert.DoesNotContain("203.0.113", fingerprint, StringComparison.Ordinal);
        Assert.Equal(64, fingerprint.Length);
    }

    [Fact]
    public void Chaves_diferentes_produzem_fingerprints_diferentes_para_o_mesmo_ip()
    {
        // E o que torna o fingerprint irreversivel: sem a chave, uma tabela
        // com o hash dos 4 bilhoes de IPv4 nao serve para nada.
        using var comUmaChave = Criar(semente: 1);
        using var comOutraChave = Criar(semente: 2);

        Assert.NotEqual(comUmaChave.Derivar("203.0.113.10"), comOutraChave.Derivar("203.0.113.10"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sem_ip_nao_ha_fingerprint(string? ip)
    {
        using var derivador = Criar();

        Assert.Null(derivador.Derivar(ip));
    }
}
