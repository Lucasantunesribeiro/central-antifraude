using System.Security.Cryptography;
using System.Text;
using CentralAntifraude.Application.Integracoes;
using CentralAntifraude.Application.Transacoes;
using CentralAntifraude.Domain.Integracoes;

namespace CentralAntifraude.Infrastructure.Integracoes;

/// <summary>
/// Geracao e conferencia de credencial de integracao.
///
/// Formato: <c>caf_{identificadorPublico}_{segredo}</c>
///
/// O identificador publico serve so para achar a linha por indice — conhece-lo
/// nao autentica nada. O segredo tem 256 bits sorteados, e apenas o SHA-256
/// dele fica no banco.
///
/// **Por que SHA-256 e nao PBKDF2** (mesma razao do refresh token, ADR 0006):
/// alongamento de chave compensa a baixa entropia de senha humana. Um valor
/// de 256 bits sorteado nao tem dicionario nem palpite, e PBKDF2 aqui
/// custaria centenas de milissegundos no caminho critico de ingestao sem
/// aumentar a seguranca.
/// </summary>
public sealed class ProtetorDeCredencial : IProtetorDeCredencial
{
    private const int BytesDoIdentificadorPublico = 8;
    private const int BytesDoSegredo = 32;
    private const char Separador = '_';

    public (string IdentificadorPublico, string HashDoSegredo, string ValorBruto) Gerar()
    {
        var identificadorPublico = Convert.ToHexStringLower(
            RandomNumberGenerator.GetBytes(BytesDoIdentificadorPublico));

        var segredo = Base64UrlEncode(RandomNumberGenerator.GetBytes(BytesDoSegredo));

        var valorBruto = string.Join(
            Separador,
            CredencialDeIntegracao.Prefixo,
            identificadorPublico,
            segredo);

        return (identificadorPublico, CalcularHash(segredo), valorBruto);
    }

    public bool TentarInterpretar(
        string? valorBruto,
        out string identificadorPublico,
        out string hashDoSegredo)
    {
        identificadorPublico = string.Empty;
        hashDoSegredo = string.Empty;

        if (string.IsNullOrWhiteSpace(valorBruto))
        {
            return false;
        }

        // Limite de 3 partes, e nao um Split livre: o segredo e base64url e
        // PODE conter o proprio separador. Sem o limite, uma chave sorteada
        // com "_" no segredo se parte em quatro pedacos e e recusada - um
        // defeito que aparece em cerca de metade das chaves geradas, o que o
        // torna intermitente e dificil de rastrear.
        var partes = valorBruto.Trim().Split(Separador, 3);

        if (partes.Length != 3 ||
            !string.Equals(partes[0], CredencialDeIntegracao.Prefixo, StringComparison.Ordinal) ||
            partes[1].Length != CredencialDeIntegracao.TamanhoDoIdentificadorPublico ||
            !partes[1].All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f') ||
            partes[2].Length == 0)
        {
            return false;
        }

        identificadorPublico = partes[1];
        hashDoSegredo = CalcularHash(partes[2]);

        return true;
    }

    private static string CalcularHash(string segredo) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(segredo)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

/// <summary>
/// Deriva o fingerprint de um endereco IP.
///
/// **Por que HMAC e nao hash simples.** O espaco de enderecos IPv4 tem 4
/// bilhoes de valores — uma tabela com o SHA-256 de todos eles cabe num disco
/// comum. Sem uma chave secreta, o "fingerprint" seria reversivel por forca
/// bruta em minutos, e guardar o hash equivaleria a guardar o IP.
///
/// Com HMAC, quem obtiver o banco sem a chave nao consegue voltar ao
/// endereco. O que continua possivel — e desejado — e responder "estas duas
/// transacoes vieram do mesmo lugar?", que e o que uma regra de risco precisa
/// (CLAUDE.md secao 57).
/// </summary>
public sealed class FingerprintDeIpComHmac : IFingerprintDeIp, IDisposable
{
    private readonly HMACSHA256 _hmac;

    public FingerprintDeIpComHmac(OpcoesDeIngestao opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        _hmac = new HMACSHA256(opcoes.LerChave());
    }

    public string? Derivar(string? enderecoIp)
    {
        if (string.IsNullOrWhiteSpace(enderecoIp))
        {
            return null;
        }

        // Normaliza antes de derivar: "192.168.0.1" e " 192.168.0.1 " sao o
        // mesmo lugar e precisam produzir o mesmo fingerprint.
        var normalizado = enderecoIp.Trim().ToLowerInvariant();

        lock (_hmac)
        {
            return Convert.ToHexStringLower(_hmac.ComputeHash(Encoding.UTF8.GetBytes(normalizado)));
        }
    }

    public void Dispose() => _hmac.Dispose();
}
