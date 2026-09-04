using CentralAntifraude.Application.Erros;
using CentralAntifraude.Application.Transacoes;
using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Integracoes;
using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.UnitTests.Aplicacao;

/// <summary>
/// O fingerprint decide se dois pedidos sao "o mesmo". Se ele for instavel,
/// um retry legitimo vira 409; se for permissivo demais, dois pedidos
/// diferentes se confundem e um deles some.
/// </summary>
public sealed class FingerprintDaRequisicaoTests
{
    private static readonly DateTimeOffset Instante =
        new(2026, 9, 3, 20, 0, 0, TimeSpan.Zero);

    private static ConteudoDaTransacao Base() => new(
        "pedido-1001",
        249.90m,
        "BRL",
        Instante,
        "cli-777",
        "pi_demo_123",
        "disp-abc",
        "203.0.113.10",
        "BR");

    [Fact]
    public void O_mesmo_conteudo_produz_o_mesmo_hash()
    {
        Assert.Equal(FingerprintDaRequisicao.Calcular(Base()), FingerprintDaRequisicao.Calcular(Base()));
    }

    [Fact]
    public void Espacos_ao_redor_nao_mudam_o_hash()
    {
        // O valor e aparado antes de persistir; se o hash considerasse o
        // espaco, um retry com formatacao diferente viraria conflito.
        var comEspacos = Base() with
        {
            IdentificadorExterno = "  pedido-1001  ",
            ClienteExternoId = " cli-777 ",
        };

        Assert.Equal(FingerprintDaRequisicao.Calcular(Base()), FingerprintDaRequisicao.Calcular(comEspacos));
    }

    [Theory]
    [InlineData("249.9")]
    [InlineData("249.90")]
    [InlineData("249.9000")]
    public void Escalas_diferentes_do_mesmo_valor_produzem_o_mesmo_hash(string valorEscrito)
    {
        // 249.9 e 249.9000 sao o mesmo dinheiro e viram a mesma linha na
        // coluna numeric(18,4). Precisam ser o mesmo pedido.
        //
        // O valor vem de string porque decimal preserva a escala do literal, e
        // [InlineData] converteria os tres para o mesmo double antes disso -
        // apagando exatamente a diferenca que este teste existe para exercitar.
        var valor = decimal.Parse(valorEscrito, System.Globalization.CultureInfo.InvariantCulture);
        var variante = Base() with { Valor = valor };

        Assert.Equal(FingerprintDaRequisicao.Calcular(Base()), FingerprintDaRequisicao.Calcular(variante));
    }

    [Fact]
    public void O_mesmo_instante_em_fusos_diferentes_produz_o_mesmo_hash()
    {
        var emOutroFuso = Base() with
        {
            OcorridaEm = Instante.ToOffset(TimeSpan.FromHours(-3)),
        };

        Assert.Equal(FingerprintDaRequisicao.Calcular(Base()), FingerprintDaRequisicao.Calcular(emOutroFuso));
    }

    [Fact]
    public void Moeda_e_pais_em_caixas_diferentes_produzem_o_mesmo_hash()
    {
        var variante = Base() with { Moeda = "brl", PaisDeOrigem = "br" };

        Assert.Equal(FingerprintDaRequisicao.Calcular(Base()), FingerprintDaRequisicao.Calcular(variante));
    }

    [Fact]
    public void Ausente_e_vazio_sao_a_mesma_coisa()
    {
        var comNulo = Base() with { FingerprintDoDispositivo = null };
        var comVazio = Base() with { FingerprintDoDispositivo = "   " };

        Assert.Equal(
            FingerprintDaRequisicao.Calcular(comNulo),
            FingerprintDaRequisicao.Calcular(comVazio));
    }

    [Fact]
    public void Qualquer_campo_diferente_muda_o_hash()
    {
        var original = FingerprintDaRequisicao.Calcular(Base());

        ConteudoDaTransacao[] variantes =
        [
            Base() with { IdentificadorExterno = "pedido-1002" },
            Base() with { Valor = 249.91m },
            Base() with { Moeda = "USD" },
            Base() with { OcorridaEm = Instante.AddSeconds(1) },
            Base() with { ClienteExternoId = "cli-778" },
            Base() with { ReferenciaDoInstrumento = "pi_demo_124" },
            Base() with { FingerprintDoDispositivo = "disp-abd" },
            Base() with { EnderecoIp = "203.0.113.11" },
            Base() with { PaisDeOrigem = "AR" },
        ];

        foreach (var variante in variantes)
        {
            Assert.NotEqual(original, FingerprintDaRequisicao.Calcular(variante));
        }
    }

    [Fact]
    public void Mover_um_caractere_entre_campos_vizinhos_nao_colide()
    {
        // A ambiguidade classica de concatenacao: "ab"+"c" e "a"+"bc" dariam
        // o mesmo texto sem separador. O separador de unidade fecha isso.
        var esquerda = Base() with { ClienteExternoId = "cli", ReferenciaDoInstrumento = "777pi" };
        var direita = Base() with { ClienteExternoId = "cli777", ReferenciaDoInstrumento = "pi" };

        Assert.NotEqual(
            FingerprintDaRequisicao.Calcular(esquerda),
            FingerprintDaRequisicao.Calcular(direita));
    }

    [Fact]
    public void O_hash_tem_o_formato_esperado_pela_coluna()
    {
        var hash = FingerprintDaRequisicao.Calcular(Base());

        Assert.Equal(64, hash.Length);
        Assert.True(hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }
}

public sealed class DetectorDePanTests
{
    [Theory]
    [InlineData("4111111111111111")]          // Visa de teste
    [InlineData("5500005555555559")]          // Mastercard de teste
    [InlineData("4111 1111 1111 1111")]       // com espacos
    [InlineData("4111-1111-1111-1111")]       // com hifens
    [InlineData("378282246310005")]           // Amex de teste, 15 digitos
    public void Reconhece_numero_de_cartao(string valor)
    {
        Assert.True(DetectorDePan.PareceNumeroDeCartao(valor));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pi_demo_123")]
    [InlineData("cli-777")]
    [InlineData("4111111111111112")]          // falha no Luhn
    [InlineData("411111111111")]              // 12 digitos, curto demais
    [InlineData("41111111111111111111")]      // 20 digitos, longo demais
    [InlineData("pi_4111111111111111")]       // token com prefixo: nao e PAN solto
    public void Nao_reconhece_o_que_nao_e_cartao(string? valor)
    {
        Assert.False(DetectorDePan.PareceNumeroDeCartao(valor));
    }

    [Fact]
    public void Token_com_prefixo_e_a_saida_para_o_falso_positivo()
    {
        // O falso positivo conhecido: um identificador puramente numerico de
        // 13 a 19 digitos pode passar no Luhn por acaso. A saida documentada
        // e usar um prefixo, e este teste garante que ela funciona.
        const string numericoQuePassaNoLuhn = "4111111111111111";

        Assert.True(DetectorDePan.PareceNumeroDeCartao(numericoQuePassaNoLuhn));
        Assert.False(DetectorDePan.PareceNumeroDeCartao($"pedido_{numericoQuePassaNoLuhn}"));
    }
}

public sealed class ValidadorDaTransacaoTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 3, 20, 0, 0, TimeSpan.Zero);

    private static readonly OpcoesDeIngestao Opcoes = new()
    {
        ChaveDeFingerprint = Convert.ToBase64String(new byte[48]),
    };

    private static ConteudoDaTransacao Valido() => new(
        "pedido-1001",
        249.90m,
        "BRL",
        Agora.AddMinutes(-1),
        "cli-777",
        "pi_demo_123",
        null,
        null,
        "BR");

    private static Dictionary<string, string[]> Erros(
        ConteudoDaTransacao conteudo,
        string? chave = "chave-valida-001")
    {
        var erro = Assert.Throws<ErroDeValidacao>(
            () => ValidadorDaTransacao.Validar(conteudo, chave, Agora, Opcoes));

        return erro.ErrosPorCampo.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    }

    [Fact]
    public void Conteudo_valido_passa()
    {
        ValidadorDaTransacao.Validar(Valido(), "chave-valida-001", Agora, Opcoes);
    }

    [Fact]
    public void Acumula_todos_os_erros_de_uma_vez()
    {
        // Um campo por resposta faria o integrador descobrir os problemas em
        // series de tentativas - e cada tentativa queima uma chave de
        // idempotencia.
        var erros = Erros(
            new ConteudoDaTransacao(null, -1, "XXXX", default, null, null, null, "nao-e-ip", "BRASIL"),
            chave: null);

        Assert.True(erros.Count >= 6, $"Esperado varios erros, veio: {string.Join(", ", erros.Keys)}");
        Assert.Contains("Idempotency-Key", erros.Keys);
        Assert.Contains("identificadorExterno", erros.Keys);
        Assert.Contains("valor", erros.Keys);
        Assert.Contains("enderecoIp", erros.Keys);
        Assert.Contains("paisDeOrigem", erros.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("curta")]
    [InlineData("com espaco no meio")]
    [InlineData("com/barra")]
    public void Recusa_chave_de_idempotencia_invalida(string? chave)
    {
        Assert.Contains("Idempotency-Key", Erros(Valido(), chave).Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Recusa_valor_nao_positivo(decimal valor)
    {
        Assert.Contains("valor", Erros(Valido() with { Valor = valor }).Keys);
    }

    [Fact]
    public void Recusa_valor_acima_do_que_a_coluna_suporta()
    {
        var acima = ValidadorDaTransacao.ValorMaximo + 1;

        Assert.Contains("valor", Erros(Valido() with { Valor = acima }).Keys);
    }

    [Fact]
    public void Recusa_valor_com_mais_casas_do_que_a_coluna()
    {
        // Sem esta guarda o banco arredondaria em silencio, gravando um valor
        // diferente do recebido.
        Assert.Contains("valor", Erros(Valido() with { Valor = 10.123456m }).Keys);
    }

    [Theory]
    [InlineData("REAL")]
    [InlineData("BR")]
    [InlineData("")]
    [InlineData("12A")]
    public void Recusa_moeda_fora_do_iso_4217(string moeda)
    {
        Assert.Contains("moeda", Erros(Valido() with { Moeda = moeda }).Keys);
    }

    [Fact]
    public void Recusa_data_alem_da_tolerancia_de_relogio()
    {
        var futuro = Agora.Add(Opcoes.ToleranciaDeRelogio).AddMinutes(1);

        Assert.Contains("ocorridaEm", Erros(Valido() with { OcorridaEm = futuro }).Keys);
    }

    [Fact]
    public void Aceita_data_dentro_da_tolerancia_de_relogio()
    {
        // Diferenca de relogio entre a origem e o servidor e normal; recusar
        // por um minuto de adiantamento quebraria integradores corretos.
        var levementeAdiantada = Agora.AddMinutes(1);

        ValidadorDaTransacao.Validar(
            Valido() with { OcorridaEm = levementeAdiantada },
            "chave-valida-001",
            Agora,
            Opcoes);
    }

    [Fact]
    public void Aceita_evento_atrasado_dentro_do_limite()
    {
        // Evento atrasado e esperado (CLAUDE.md secao 16) e precisa entrar.
        var atrasada = Agora.AddDays(-Opcoes.AtrasoMaximoEmDias + 1);

        ValidadorDaTransacao.Validar(
            Valido() with { OcorridaEm = atrasada },
            "chave-valida-001",
            Agora,
            Opcoes);
    }

    [Fact]
    public void Recusa_data_absurdamente_antiga()
    {
        var antiga = Agora.AddDays(-Opcoes.AtrasoMaximoEmDias - 1);

        Assert.Contains("ocorridaEm", Erros(Valido() with { OcorridaEm = antiga }).Keys);
    }

    [Theory]
    [InlineData("nao-e-ip")]
    [InlineData("999.999.999.999")]
    public void Recusa_endereco_ip_invalido(string ip)
    {
        Assert.Contains("enderecoIp", Erros(Valido() with { EnderecoIp = ip }).Keys);
    }

    [Theory]
    [InlineData("BRASIL")]
    [InlineData("B")]
    [InlineData("12")]
    public void Recusa_pais_fora_do_iso_3166(string pais)
    {
        Assert.Contains("paisDeOrigem", Erros(Valido() with { PaisDeOrigem = pais }).Keys);
    }

    [Fact]
    public void Recusa_numero_de_cartao_em_campo_de_texto()
    {
        var comPan = Valido() with { ReferenciaDoInstrumento = "4111111111111111" };

        var erros = Erros(comPan);

        Assert.Contains("referenciaDoInstrumento", erros.Keys);
        Assert.Contains("cartao", erros["referenciaDoInstrumento"][0], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("com espaco")]
    [InlineData("com'aspas")]
    [InlineData("<script>")]
    public void Recusa_identificador_com_caractere_fora_do_conjunto(string valor)
    {
        Assert.Contains("identificadorExterno", Erros(Valido() with { IdentificadorExterno = valor }).Keys);
    }
}

public sealed class OpcoesDeIngestaoTests
{
    private static OpcoesDeIngestao Validas() => new()
    {
        ChaveDeFingerprint = Convert.ToBase64String(new byte[48]),
    };

    [Fact]
    public void Configuracao_valida_passa()
    {
        Validas().Validar();
    }

    [Fact]
    public void Chave_de_fingerprint_ausente_derruba_a_inicializacao()
    {
        // Falha fechada: sem chave, um "fingerprint" de IP derivado de chave
        // vazia seria reversivel por forca bruta - ou seja, equivaleria a
        // guardar o IP.
        var opcoes = Validas();
        opcoes.ChaveDeFingerprint = string.Empty;

        var erro = Assert.Throws<InvalidOperationException>(opcoes.Validar);
        Assert.Contains("ChaveDeFingerprint", erro.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Chave_curta_demais_e_recusada()
    {
        var opcoes = Validas();
        opcoes.ChaveDeFingerprint = Convert.ToBase64String(new byte[16]);

        Assert.Throws<InvalidOperationException>(opcoes.Validar);
    }
}

public sealed class TransacaoTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 3, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid Organizacao = Identificador.Novo();
    private static readonly Guid Integracao = Identificador.Novo();

    private static Transacao Registrar(decimal valor = 10m) => Transacao.Registrar(
        Organizacao,
        Integracao,
        "pedido-1",
        Dinheiro.De(valor, "BRL"),
        Agora.AddMinutes(-5),
        Agora,
        "cli-1",
        "pi_1",
        null,
        null,
        "BR",
        "chave-000001",
        new string('a', 64));

    [Fact]
    public void Guarda_os_dois_tempos_separadamente()
    {
        var transacao = Registrar();

        Assert.Equal(Agora.AddMinutes(-5), transacao.OcorridaEm);
        Assert.Equal(Agora, transacao.RecebidaEm);
        Assert.Equal(TimeSpan.FromMinutes(5), transacao.AtrasoAteRecebimento);
    }

    [Fact]
    public void Normaliza_os_tempos_para_utc()
    {
        var transacao = Transacao.Registrar(
            Organizacao,
            Integracao,
            "pedido-2",
            Dinheiro.De(10m, "BRL"),
            Agora.ToOffset(TimeSpan.FromHours(-3)),
            Agora,
            "cli-1",
            "pi_1",
            null,
            null,
            null,
            "chave-000002",
            new string('a', 64));

        Assert.Equal(TimeSpan.Zero, transacao.OcorridaEm.Offset);
        Assert.Equal(TimeSpan.Zero, transacao.RecebidaEm.Offset);
    }

    [Fact]
    public void Recusa_valor_nao_positivo()
    {
        Assert.Throws<ViolacaoDeInvariante>(() => Registrar(0m));
        Assert.Throws<ViolacaoDeInvariante>(() => Registrar(-1m));
    }

    [Fact]
    public void Recusa_transacao_sem_organizacao_ou_integracao()
    {
        Assert.Throws<ViolacaoDeInvariante>(() => Transacao.Registrar(
            Guid.Empty, Integracao, "p", Dinheiro.De(1m, "BRL"), Agora, Agora,
            "c", "pi", null, null, null, "chave-000003", "hash"));
    }

    [Fact]
    public void Normaliza_o_pais_para_maiusculas()
    {
        var transacao = Transacao.Registrar(
            Organizacao, Integracao, "p", Dinheiro.De(1m, "BRL"), Agora, Agora,
            "c", "pi", null, null, " br ", "chave-000004", "hash");

        Assert.Equal("BR", transacao.PaisDeOrigem);
    }
}

public sealed class CredencialDeIntegracaoTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 3, 20, 0, 0, TimeSpan.Zero);

    private static CredencialDeIntegracao Criar() => CredencialDeIntegracao.Criar(
        Identificador.Novo(),
        Identificador.Novo(),
        "0123456789abcdef",
        new string('a', 64),
        Agora);

    [Fact]
    public void Nasce_valida_e_sem_uso()
    {
        var credencial = Criar();

        Assert.False(credencial.EstaRevogada);
        Assert.Null(credencial.UsadaPelaUltimaVezEm);
    }

    [Fact]
    public void Registrar_uso_grava_no_maximo_uma_vez_por_minuto()
    {
        // Um UPDATE por requisicao no caminho critico de ingestao seria custo
        // puro; a informacao serve para decidir sobre revogacao, e granularidade
        // de minuto basta para isso.
        var credencial = Criar();

        Assert.True(credencial.RegistrarUso(Agora));
        Assert.False(credencial.RegistrarUso(Agora.AddSeconds(30)));
        Assert.True(credencial.RegistrarUso(Agora.AddMinutes(2)));
    }

    [Fact]
    public void Revogar_preserva_o_primeiro_motivo()
    {
        var credencial = Criar();

        credencial.Revogar(MotivoDeRevogacaoDeCredencial.RevogadaManualmente, Agora);
        credencial.Revogar(MotivoDeRevogacaoDeCredencial.IntegracaoDesativada, Agora.AddHours(1));

        Assert.Equal(MotivoDeRevogacaoDeCredencial.RevogadaManualmente, credencial.MotivoDaRevogacao);
        Assert.Equal(Agora, credencial.RevogadaEm);
    }

    [Fact]
    public void Recusa_credencial_sem_hash()
    {
        Assert.Throws<ViolacaoDeInvariante>(() => CredencialDeIntegracao.Criar(
            Identificador.Novo(), Identificador.Novo(), "0123456789abcdef", "  ", Agora));
    }
}
