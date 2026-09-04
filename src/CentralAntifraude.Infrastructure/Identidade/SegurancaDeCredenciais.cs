using System.Security.Cryptography;
using CentralAntifraude.Application.Identidade;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CentralAntifraude.Infrastructure.Identidade;

/// <summary>
/// Hash de senha via PBKDF2-HMAC-SHA512.
///
/// Usa o <see cref="PasswordHasher{TUser}"/> do ASP.NET Core em vez de
/// implementacao propria. Ele ja resolve, com codigo revisado, tres coisas
/// faceis de errar sozinho: salt aleatorio por senha, comparacao em tempo
/// constante e um byte de versao no inicio do hash, que permite trocar
/// parametros no futuro sem invalidar as senhas existentes.
///
/// **Parametros.** O formato V3 do ASP.NET Core e PBKDF2-HMAC-SHA512; o
/// padrao da biblioteca e 100.000 iteracoes. A OWASP recomenda 220.000 para
/// PBKDF2-HMAC-SHA512, e e esse o valor configurado em
/// <see cref="InjecaoDeDependencia"/>.
///
/// **Fonte:** OWASP Password Storage Cheat Sheet, consultado em 2026-09-03.
/// A mesma folha coloca o Argon2id como primeira escolha; ele exigiria
/// dependencia de terceiro e fica registrado como caminho de evolucao no
/// ADR 0006, nao como pendencia de seguranca.
/// </summary>
public sealed class HashDeSenhaPbkdf2 : IHashDeSenha
{
    // O PasswordHasher e generico sobre o tipo de usuario, mas nunca usa a
    // instancia - so o algoritmo. Este marcador evita arrastar a entidade de
    // dominio para dentro de um detalhe de criptografia.
    private sealed class Portador;

    private readonly PasswordHasher<Portador> _hasher;
    private static readonly Portador Instancia = new();

    public HashDeSenhaPbkdf2(IOptions<PasswordHasherOptions> opcoes)
    {
        _hasher = new PasswordHasher<Portador>(opcoes);
    }

    public string Gerar(string senha)
    {
        ArgumentException.ThrowIfNullOrEmpty(senha);

        return _hasher.HashPassword(Instancia, senha);
    }

    public ResultadoDaVerificacaoDeSenha Verificar(string hashArmazenado, string senhaInformada)
    {
        if (string.IsNullOrEmpty(hashArmazenado) || string.IsNullOrEmpty(senhaInformada))
        {
            return ResultadoDaVerificacaoDeSenha.Invalida;
        }

        // Um hash corrompido no banco nao pode derrubar o login com 500 -
        // isso viraria um oraculo sobre o estado da conta.
        try
        {
            return _hasher.VerifyHashedPassword(Instancia, hashArmazenado, senhaInformada) switch
            {
                PasswordVerificationResult.Success => ResultadoDaVerificacaoDeSenha.Valida,
                PasswordVerificationResult.SuccessRehashNeeded =>
                    ResultadoDaVerificacaoDeSenha.ValidaMasPrecisaRegravar,
                _ => ResultadoDaVerificacaoDeSenha.Invalida,
            };
        }
        catch (FormatException)
        {
            return ResultadoDaVerificacaoDeSenha.Invalida;
        }
    }
}

/// <summary>
/// Geracao e conferencia do refresh token.
///
/// O token e 256 bits de aleatoriedade criptografica, em base64url. Nao
/// carrega informacao nenhuma — nao ha o que ler nem o que forjar dentro
/// dele, so conferir contra a linha do banco.
///
/// **Por que SHA-256 puro e nao PBKDF2 aqui.** Alongamento de chave existe
/// para compensar a baixa entropia de senhas escolhidas por pessoas. Um valor
/// de 256 bits sorteado nao tem esse problema: nao ha dicionario, nao ha
/// palpite. Usar PBKDF2 custaria centenas de milissegundos por renovacao sem
/// aumentar a seguranca em nada.
/// </summary>
public sealed class ProtetorDeRefreshToken : IProtetorDeRefreshToken
{
    private const int TamanhoEmBytes = 32;

    public (string Bruto, string Hash) Gerar()
    {
        var bytes = RandomNumberGenerator.GetBytes(TamanhoEmBytes);

        // Base64url: o valor viaja em cookie, onde "+", "/" e "=" exigiriam
        // escape e convidam a bug de codificacao.
        var bruto = Base64UrlEncode(bytes);

        return (bruto, CalcularHash(bruto));
    }

    public string CalcularHash(string tokenBruto)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenBruto);

        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tokenBruto));

        return Convert.ToHexStringLower(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
