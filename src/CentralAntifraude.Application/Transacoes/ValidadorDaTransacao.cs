using System.Net;
using CentralAntifraude.Application.Erros;
using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.Application.Transacoes;

/// <summary>
/// Limites de sanidade da ingestao.
///
/// Estes numeros NAO sao regra antifraude — sao o que separa um pedido
/// plausivel de lixo evidente. Regra de risco comeca na Fase 3, e la os
/// limites pertencem ao perfil de risco do tenant, nao a borda.
/// </summary>
public sealed class OpcoesDeIngestao
{
    public const string Secao = "Ingestao";

    /// <summary>
    /// Quanto uma transacao pode estar no futuro.
    ///
    /// Existe por causa de diferenca de relogio entre o sistema de origem e o
    /// nosso, nao por generosidade: aceitar horas no futuro envenenaria as
    /// janelas temporais das regras de velocidade.
    /// </summary>
    public int ToleranciaDeRelogioEmMinutos { get; set; } = 5;

    /// <summary>
    /// Quao antiga uma transacao pode ser.
    ///
    /// Evento atrasado e esperado e deve ser aceito (CLAUDE.md secao 16). O
    /// teto existe apenas para barrar data claramente errada — um ano de
    /// 1970 vindo de um campo nao preenchido, por exemplo.
    /// </summary>
    public int AtrasoMaximoEmDias { get; set; } = 30;

    /// <summary>
    /// Chave HMAC usada para derivar o fingerprint de IP, em base64.
    ///
    /// O IP em si nunca e persistido (CLAUDE.md secao 57). O HMAC responde
    /// "mesmo lugar?" sem guardar o dado que identifica a pessoa.
    /// </summary>
    public string ChaveDeFingerprint { get; set; } = string.Empty;

    public TimeSpan ToleranciaDeRelogio => TimeSpan.FromMinutes(ToleranciaDeRelogioEmMinutos);

    public TimeSpan AtrasoMaximo => TimeSpan.FromDays(AtrasoMaximoEmDias);

    public void Validar()
    {
        if (ToleranciaDeRelogioEmMinutos is < 0 or > 60)
        {
            throw new InvalidOperationException(
                $"{Secao}:ToleranciaDeRelogioEmMinutos deve estar entre 0 e 60.");
        }

        if (AtrasoMaximoEmDias is < 1 or > 3650)
        {
            throw new InvalidOperationException(
                $"{Secao}:AtrasoMaximoEmDias deve estar entre 1 e 3650.");
        }

        if (string.IsNullOrWhiteSpace(ChaveDeFingerprint))
        {
            throw new InvalidOperationException(
                $"{Secao}:ChaveDeFingerprint nao configurada. Defina a variavel de ambiente " +
                $"{Secao}__ChaveDeFingerprint ou use `dotnet user-secrets`. Ver docs/setup-local.md.");
        }

        if (LerChaveOuNulo() is not { Length: >= 32 })
        {
            throw new InvalidOperationException(
                $"{Secao}:ChaveDeFingerprint precisa ser base64 com ao menos 32 bytes.");
        }
    }

    public byte[] LerChave() =>
        LerChaveOuNulo()
        ?? throw new InvalidOperationException($"{Secao}:ChaveDeFingerprint invalida.");

    private byte[]? LerChaveOuNulo()
    {
        var destino = new byte[((ChaveDeFingerprint.Length + 3) / 4) * 3];

        return Convert.TryFromBase64String(ChaveDeFingerprint, destino, out var escritos)
            ? destino[..escritos]
            : null;
    }
}

/// <summary>
/// Valida o conteudo de uma tentativa de pagamento na borda.
///
/// Acumula TODOS os erros antes de recusar. Devolver um campo por vez faria o
/// integrador descobrir os problemas em series de tentativas — e cada
/// tentativa e uma requisicao com chave de idempotencia queimada.
/// </summary>
public static class ValidadorDaTransacao
{
    /// <summary>
    /// Teto imposto pela coluna <c>numeric(18,4)</c>: 14 digitos inteiros.
    /// Nao e regra de negocio — e o limite fisico do tipo.
    /// </summary>
    public const decimal ValorMaximo = 99_999_999_999_999m;

    public const int TamanhoMinimoDaChaveDeIdempotencia = 8;

    public static void Validar(
        ConteudoDaTransacao conteudo,
        string? chaveDeIdempotencia,
        DateTimeOffset agora,
        OpcoesDeIngestao opcoes)
    {
        ArgumentNullException.ThrowIfNull(conteudo);
        ArgumentNullException.ThrowIfNull(opcoes);

        var erros = new Dictionary<string, string[]>(StringComparer.Ordinal);

        ValidarChaveDeIdempotencia(chaveDeIdempotencia, erros);
        ValidarIdentificadores(conteudo, erros);
        ValidarValor(conteudo, erros);
        ValidarTempo(conteudo, agora, opcoes, erros);
        ValidarOrigem(conteudo, erros);

        if (erros.Count > 0)
        {
            throw new ErroDeValidacao("A transacao contem campos invalidos.", erros);
        }
    }

    private static void ValidarChaveDeIdempotencia(
        string? chave,
        Dictionary<string, string[]> erros)
    {
        if (string.IsNullOrWhiteSpace(chave))
        {
            erros["Idempotency-Key"] =
                ["Cabecalho obrigatorio. Envie um valor unico por tentativa de pagamento."];
            return;
        }

        var limpa = chave.Trim();

        if (limpa.Length < TamanhoMinimoDaChaveDeIdempotencia ||
            limpa.Length > Transacao.TamanhoMaximoDeIdentificadorExterno)
        {
            erros["Idempotency-Key"] =
            [
                $"Deve ter entre {TamanhoMinimoDaChaveDeIdempotencia} e " +
                $"{Transacao.TamanhoMaximoDeIdentificadorExterno} caracteres."
            ];
            return;
        }

        if (!EhIdentificadorSeguro(limpa))
        {
            erros["Idempotency-Key"] = ["Aceita apenas letras, digitos, hifen, sublinhado e dois-pontos."];
        }
    }

    private static void ValidarIdentificadores(
        ConteudoDaTransacao conteudo,
        Dictionary<string, string[]> erros)
    {
        ValidarIdentificador(conteudo.IdentificadorExterno, "identificadorExterno", erros);
        ValidarIdentificador(conteudo.ClienteExternoId, "clienteExternoId", erros);
        ValidarIdentificador(conteudo.ReferenciaDoInstrumento, "referenciaDoInstrumento", erros);

        // Defesa em profundidade contra PAN (CLAUDE.md secoes 56 e 58).
        // O contrato nao tem campo de cartao, mas nada impediria alguem de
        // colocar o numero em um campo de texto. Ver DetectorDePan para o
        // falso positivo conhecido e como evita-lo.
        RecusarSeParecerCartao(conteudo.ReferenciaDoInstrumento, "referenciaDoInstrumento", erros);
        RecusarSeParecerCartao(conteudo.ClienteExternoId, "clienteExternoId", erros);
        RecusarSeParecerCartao(conteudo.IdentificadorExterno, "identificadorExterno", erros);

        if (conteudo.FingerprintDoDispositivo is { } dispositivo &&
            !string.IsNullOrWhiteSpace(dispositivo) &&
            dispositivo.Trim().Length > Transacao.TamanhoMaximoDeFingerprint)
        {
            erros["fingerprintDoDispositivo"] =
                [$"Deve ter no maximo {Transacao.TamanhoMaximoDeFingerprint} caracteres."];
        }
    }

    private static void ValidarIdentificador(
        string? valor,
        string campo,
        Dictionary<string, string[]> erros)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            erros[campo] = ["Campo obrigatorio."];
            return;
        }

        var limpo = valor.Trim();

        if (limpo.Length > Transacao.TamanhoMaximoDeIdentificadorExterno)
        {
            erros[campo] =
                [$"Deve ter no maximo {Transacao.TamanhoMaximoDeIdentificadorExterno} caracteres."];
            return;
        }

        if (!EhIdentificadorSeguro(limpo))
        {
            erros[campo] = ["Aceita apenas letras, digitos, hifen, sublinhado e dois-pontos."];
        }
    }

    private static void ValidarValor(ConteudoDaTransacao conteudo, Dictionary<string, string[]> erros)
    {
        if (conteudo.Valor <= 0)
        {
            erros["valor"] = ["Deve ser maior que zero."];
        }
        else if (conteudo.Valor > ValorMaximo)
        {
            erros["valor"] = ["Valor acima do maximo suportado."];
        }

        if (!Dinheiro.TentarCriar(conteudo.Valor, conteudo.Moeda, out _, out var erroDeMoeda))
        {
            // A mensagem de Dinheiro cobre moeda invalida e escala excessiva;
            // atribuir ao campo certo depende de qual delas falhou.
            var campo = erroDeMoeda.Contains("decimais", StringComparison.OrdinalIgnoreCase)
                ? "valor"
                : "moeda";

            if (!erros.ContainsKey(campo))
            {
                erros[campo] = [erroDeMoeda];
            }
        }
    }

    private static void ValidarTempo(
        ConteudoDaTransacao conteudo,
        DateTimeOffset agora,
        OpcoesDeIngestao opcoes,
        Dictionary<string, string[]> erros)
    {
        var ocorridaEm = conteudo.OcorridaEm.ToUniversalTime();

        if (ocorridaEm == default)
        {
            erros["ocorridaEm"] = ["Campo obrigatorio."];
            return;
        }

        if (ocorridaEm > agora + opcoes.ToleranciaDeRelogio)
        {
            erros["ocorridaEm"] =
            [
                "Data no futuro alem da tolerancia de relogio " +
                $"({opcoes.ToleranciaDeRelogioEmMinutos} minutos)."
            ];
            return;
        }

        if (ocorridaEm < agora - opcoes.AtrasoMaximo)
        {
            erros["ocorridaEm"] = [$"Data anterior ao limite de {opcoes.AtrasoMaximoEmDias} dias."];
        }
    }

    private static void ValidarOrigem(ConteudoDaTransacao conteudo, Dictionary<string, string[]> erros)
    {
        if (!string.IsNullOrWhiteSpace(conteudo.EnderecoIp) &&
            !IPAddress.TryParse(conteudo.EnderecoIp.Trim(), out _))
        {
            erros["enderecoIp"] = ["Endereco IP invalido."];
        }

        if (string.IsNullOrWhiteSpace(conteudo.PaisDeOrigem))
        {
            return;
        }

        var pais = conteudo.PaisDeOrigem.Trim();

        if (pais.Length != 2 || !pais.All(char.IsAsciiLetter))
        {
            erros["paisDeOrigem"] = ["Use o codigo ISO 3166-1 alfa-2, com duas letras."];
        }
    }

    /// <summary>
    /// Conjunto fechado de caracteres para identificadores.
    ///
    /// Restringir na borda evita que texto arbitrario alcance log, cabecalho
    /// de resposta ou tela — e um identificador externo legitimo nunca precisa
    /// de espaco, aspas ou caractere de controle.
    /// </summary>
    private static bool EhIdentificadorSeguro(string valor) =>
        valor.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.');

    private static void RecusarSeParecerCartao(
        string? valor,
        string campo,
        Dictionary<string, string[]> erros)
    {
        if (erros.ContainsKey(campo) || !DetectorDePan.PareceNumeroDeCartao(valor))
        {
            return;
        }

        erros[campo] =
        [
            "O valor parece um numero de cartao. A Central Antifraude nao aceita " +
            "dados completos de cartao — envie a referencia tokenizada do seu sistema."
        ];
    }
}

/// <summary>
/// Reconhece um numero de cartao em texto livre.
///
/// Usa o algoritmo de Luhn sobre sequencias de 13 a 19 digitos — a mesma
/// verificacao que as bandeiras usam. Nao e criptografia nem heuristica de
/// fraude: e uma barreira contra o acidente de um integrador colar o PAN em
/// um campo de identificador.
///
/// **Falso positivo conhecido.** Um identificador puramente numerico de 13 a
/// 19 digitos tem cerca de 1 chance em 10 de passar no Luhn por acaso e ser
/// recusado. O custo e assumido de proposito: recusar um identificador
/// numerico legitimo gera um erro claro, com orientacao na mensagem; aceitar
/// um PAN gera um dado que nao deveria existir no banco e que nao ha como
/// desfazer. A saida para o integrador e simples — usar um token com prefixo,
/// que e a pratica esperada de qualquer forma.
/// </summary>
public static class DetectorDePan
{
    private const int MinimoDeDigitos = 13;
    private const int MaximoDeDigitos = 19;

    public static bool PareceNumeroDeCartao(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return false;
        }

        // Separadores comuns em numero de cartao digitado sao ignorados, para
        // que "4111 1111 1111 1111" e "4111-1111-1111-1111" tambem sejam
        // reconhecidos.
        Span<char> digitos = stackalloc char[MaximoDeDigitos + 1];
        var quantidade = 0;

        foreach (var caractere in valor)
        {
            if (char.IsAsciiDigit(caractere))
            {
                if (quantidade > MaximoDeDigitos)
                {
                    return false;
                }

                digitos[quantidade++] = caractere;
                continue;
            }

            if (caractere is ' ' or '-')
            {
                continue;
            }

            // Qualquer outro caractere descaracteriza a sequencia: um token
            // como "pi_demo_4111111111111111" nao e um numero de cartao solto.
            return false;
        }

        return quantidade is >= MinimoDeDigitos and <= MaximoDeDigitos &&
               PassaNoLuhn(digitos[..quantidade]);
    }

    private static bool PassaNoLuhn(ReadOnlySpan<char> digitos)
    {
        var soma = 0;
        var dobrar = false;

        for (var i = digitos.Length - 1; i >= 0; i--)
        {
            var valor = digitos[i] - '0';

            if (dobrar)
            {
                valor *= 2;

                if (valor > 9)
                {
                    valor -= 9;
                }
            }

            soma += valor;
            dobrar = !dobrar;
        }

        return soma % 10 == 0;
    }
}
