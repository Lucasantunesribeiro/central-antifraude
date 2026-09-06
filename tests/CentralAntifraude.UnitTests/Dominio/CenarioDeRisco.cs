using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Transacoes;

namespace CentralAntifraude.UnitTests.Dominio;

/// <summary>
/// Monta transacoes e historicos para os testes do motor.
///
/// Tudo aqui e explicito e sem relogio: os instantes sao calculados a partir
/// de <see cref="Referencia"/>, uma data fixa. Um teste de motor que dependa
/// de "agora" deixaria de ser deterministico exatamente naquilo que ele
/// deveria estar provando.
/// </summary>
internal static class CenarioDeRisco
{
    public static readonly DateTimeOffset Referencia =
        new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

    public static readonly Guid Organizacao = Identificador.Novo();

    public static Transacao Transacao(
        decimal valor = 100m,
        string moeda = "BRL",
        DateTimeOffset? ocorridaEm = null,
        string cliente = "cliente-1",
        string? dispositivo = "disp-conhecido",
        string? pais = "BR",
        Guid? organizacaoId = null)
    {
        var instante = ocorridaEm ?? Referencia;

        return Domain.Transacoes.Transacao.Registrar(
            organizacaoId ?? Organizacao,
            Identificador.Novo(),
            $"ext-{Identificador.Novo():N}",
            Dinheiro.De(valor, moeda),
            instante,
            recebidaEm: instante,
            cliente,
            "pi_demo_1",
            dispositivo,
            fingerprintDoIp: null,
            pais,
            $"idem-{Identificador.Novo():N}",
            "fingerprint");
    }

    public static TransacaoDoHistorico Anterior(
        double minutosAntes,
        decimal valor = 100m,
        string moeda = "BRL",
        string? dispositivo = "disp-conhecido",
        string? pais = "BR") =>
        new(
            Referencia.AddMinutes(-minutosAntes),
            valor,
            moeda,
            dispositivo,
            pais);

    public static ContextoDeRisco Contexto(params TransacaoDoHistorico[] anteriores) =>
        new(anteriores);

    /// <summary>
    /// Uma versao de perfil com as regras informadas, na organizacao padrao.
    /// </summary>
    public static VersaoDePerfilDeRisco Perfil(
        IReadOnlyList<(TipoDeRegra Tipo, ConfiguracaoDeRegra Config, int Pontos)> regras,
        int limiarDeRevisao = 40,
        int limiarDeBloqueio = 70,
        Guid? organizacaoId = null)
    {
        var organizacao = organizacaoId ?? Organizacao;
        var perfil = PerfilDeRisco.Criar(organizacao, "Perfil de teste", Referencia);

        var versoes = regras
            .Select(r => Regra
                .Criar(organizacao, r.Tipo, r.Tipo.ToString(), r.Config, r.Pontos, Referencia)
                .PublicarRascunho(ultimaPublicada: null, Referencia))
            .ToList();

        return VersaoDePerfilDeRisco.Publicar(
            perfil,
            numero: 1,
            limiarDeRevisao,
            limiarDeBloqueio,
            versoes,
            Referencia);
    }

    /// <summary>Perfil com o catalogo padrao completo, para os testes de ponta a ponta do motor.</summary>
    public static VersaoDePerfilDeRisco PerfilPadrao(Guid? organizacaoId = null) =>
        CatalogoPadraoDeRisco
            .Provisionar(organizacaoId ?? Organizacao, Referencia)
            .VersaoDoPerfil;
}
