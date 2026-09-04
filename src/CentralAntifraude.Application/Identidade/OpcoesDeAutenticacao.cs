namespace CentralAntifraude.Application.Identidade;

/// <summary>
/// Parametros da autenticacao humana.
///
/// Fica no Application, e nao na Infrastructure, porque dois lados precisam
/// concordar sobre eles: quem emite o access token (Infrastructure) e quem o
/// valida (Api). Se cada um lesse a sua propria configuracao, um emissor
/// divergente so apareceria como "token invalido" sem explicacao.
/// </summary>
public sealed class OpcoesDeAutenticacao
{
    public const string Secao = "Autenticacao";

    /// <summary>Tamanho minimo da chave para HMAC-SHA256 (RFC 7518, secao 3.2).</summary>
    public const int TamanhoMinimoDaChaveEmBytes = 32;

    public string Emissor { get; set; } = "central-antifraude";

    public string Audiencia { get; set; } = "central-antifraude";

    /// <summary>
    /// Chave simetrica de assinatura, em base64. Vem de variavel de ambiente
    /// ou user-secrets — nunca de arquivo versionado.
    /// </summary>
    public string ChaveDeAssinatura { get; set; } = string.Empty;

    /// <summary>
    /// Vida do access token. Curta de proposito: ele viaja em toda requisicao
    /// e vive na memoria do navegador, entao o dano de um vazamento e limitado
    /// pelo relogio. A continuidade da sessao vem da rotacao do refresh token.
    /// </summary>
    public int MinutosDoAccessToken { get; set; } = 15;

    /// <summary>Vida do refresh token, contada a partir de cada rotacao.</summary>
    public int DiasDoRefreshToken { get; set; } = 14;

    /// <summary>
    /// Origens aceitas nos endpoints que dependem do cookie de sessao.
    ///
    /// E a defesa contra CSRF que funciona tanto em mesma origem quanto em
    /// deploy cross-site: um site malicioso nao consegue forjar o cabecalho
    /// Origin que o navegador envia. Vazia significa "aceitar apenas
    /// requisicao de mesma origem".
    /// </summary>
    public string[] OrigensPermitidas { get; set; } = [];

    public TimeSpan ValidadeDoAccessToken => TimeSpan.FromMinutes(MinutosDoAccessToken);

    public TimeSpan ValidadeDoRefreshToken => TimeSpan.FromDays(DiasDoRefreshToken);

    /// <summary>
    /// Validacao chamada na composicao da aplicacao. Falha fechada: uma
    /// configuracao de autenticacao errada precisa impedir a aplicacao de
    /// subir, e nao virar token invalido em producao.
    /// </summary>
    public void Validar()
    {
        if (string.IsNullOrWhiteSpace(Emissor) || string.IsNullOrWhiteSpace(Audiencia))
        {
            throw new InvalidOperationException(
                $"{Secao}: Emissor e Audiencia sao obrigatorios.");
        }

        if (string.IsNullOrWhiteSpace(ChaveDeAssinatura))
        {
            throw new InvalidOperationException(
                $"{Secao}:ChaveDeAssinatura nao configurada. Defina a variavel de ambiente " +
                $"{Secao}__ChaveDeAssinatura ou use `dotnet user-secrets`. Ver docs/setup-local.md.");
        }

        if (!TentarLerChave(out var bytes))
        {
            throw new InvalidOperationException(
                $"{Secao}:ChaveDeAssinatura precisa estar em base64.");
        }

        if (bytes.Length < TamanhoMinimoDaChaveEmBytes)
        {
            throw new InvalidOperationException(
                $"{Secao}:ChaveDeAssinatura precisa ter ao menos " +
                $"{TamanhoMinimoDaChaveEmBytes} bytes ({bytes.Length} informados).");
        }

        if (MinutosDoAccessToken is < 1 or > 60)
        {
            throw new InvalidOperationException(
                $"{Secao}:MinutosDoAccessToken deve estar entre 1 e 60.");
        }

        if (DiasDoRefreshToken is < 1 or > 90)
        {
            throw new InvalidOperationException(
                $"{Secao}:DiasDoRefreshToken deve estar entre 1 e 90.");
        }
    }

    public byte[] LerChave() =>
        TentarLerChave(out var bytes)
            ? bytes
            : throw new InvalidOperationException($"{Secao}:ChaveDeAssinatura precisa estar em base64.");

    private bool TentarLerChave(out byte[] bytes)
    {
        var destino = new byte[((ChaveDeAssinatura.Length + 3) / 4) * 3];

        if (Convert.TryFromBase64String(ChaveDeAssinatura, destino, out var escritos))
        {
            bytes = destino[..escritos];
            return true;
        }

        bytes = [];
        return false;
    }
}
