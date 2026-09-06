using CentralAntifraude.Application.Risco;
using CentralAntifraude.Domain.Risco;

namespace CentralAntifraude.Api.Risco;

// ---------------------------------------------------------------------------
// Administracao de regras (Fase 8).
//
// **O que entra sao numeros nomeados, nunca logica.** A configuracao chega
// como um dicionario de nomes conhecidos para valores decimais, e
// `CatalogoDeTiposDeRegra.TentarMontar` transforma isso no record tipado
// depois de recusar campo desconhecido, campo faltando e valor fora da faixa.
// Nao ha expressao, SQL nem script em nenhum ponto (CLAUDE.md secao 21).
//
// **Nenhum campo interno entra pelo corpo.** Nao ha `status`, `numero`,
// `organizacaoId` nem `publicadaEm` em requisicao nenhuma; o JSON estrito
// recusa o que nao esta declarado. `versao` esta em toda alteracao e nao e
// mass assignment: ela nunca e atribuida, so comparada (CLAUDE.md secao 53).
// ---------------------------------------------------------------------------

/// <summary>Cria uma regra como rascunho.</summary>
public sealed record CriarRegraRequisicao(
    string? Tipo,
    string? Nome,
    Dictionary<string, decimal>? Configuracao,
    int Pontos);

/// <summary>Grava o rascunho de uma regra existente.</summary>
public sealed record RascunhoDeRegraRequisicao(
    string? Nome,
    Dictionary<string, decimal>? Configuracao,
    int Pontos,
    int Versao);

/// <summary>Acao que so precisa saber sobre qual estado ela age.</summary>
public sealed record AcaoNaRegraRequisicao(int Versao);

/// <summary>Liga ou desliga a regra.</summary>
public sealed record AtivacaoDaRegraRequisicao(bool Ativa, int Versao);

/// <summary>
/// Publica uma versao de perfil com limiares novos.
///
/// <see cref="NumeroDaVersaoVigente"/> e o token de concorrencia do perfil:
/// dois supervisores mexendo nos limiares ao mesmo tempo nao podem se
/// sobrescrever em silencio.
/// </summary>
public sealed record LimiaresRequisicao(
    int LimiarDeRevisao,
    int LimiarDeBloqueio,
    int NumeroDaVersaoVigente);

/// <summary>Um campo configuravel, do jeito que a tela precisa para montar o formulario.</summary>
public sealed record CampoDeConfiguracaoResposta(
    string Nome,
    string Rotulo,
    string Tipo,
    decimal Minimo,
    decimal Maximo,
    decimal Padrao)
{
    public static CampoDeConfiguracaoResposta De(CampoDeConfiguracao campo)
    {
        ArgumentNullException.ThrowIfNull(campo);

        return new CampoDeConfiguracaoResposta(
            campo.Nome,
            campo.Rotulo,
            campo.Tipo.ToString(),
            campo.Minimo,
            campo.Maximo,
            campo.Padrao);
    }
}

/// <summary>
/// Um tipo do catalogo fechado.
///
/// A tela monta o formulario a partir disto — e so disto. Sem esta rota, o
/// frontend teria que repetir os limites de cada campo, e as duas listas
/// sairiam de sincronia no primeiro ajuste (ROADMAP 8.3).
/// </summary>
public sealed record TipoDeRegraResposta(
    string Tipo,
    string Rotulo,
    string Resumo,
    int PontosSugeridos,
    IReadOnlyList<CampoDeConfiguracaoResposta> Campos)
{
    public static TipoDeRegraResposta De(DescricaoDeTipoDeRegra descricao)
    {
        ArgumentNullException.ThrowIfNull(descricao);

        return new TipoDeRegraResposta(
            descricao.Tipo.ToString(),
            descricao.Rotulo,
            descricao.Resumo,
            descricao.PontosSugeridos,
            [.. descricao.Campos.Select(CampoDeConfiguracaoResposta.De)]);
    }
}

/// <summary>Uma versao publicada, no historico da regra.</summary>
public sealed record VersaoDeRegraResposta(
    Guid Id,
    int Numero,
    int Pontos,
    string Configuracao,
    IReadOnlyDictionary<string, decimal> Valores,
    DateTimeOffset PublicadaEm)
{
    public static VersaoDeRegraResposta De(VersaoDeRegra versao)
    {
        ArgumentNullException.ThrowIfNull(versao);

        return new VersaoDeRegraResposta(
            versao.Id,
            versao.Numero,
            versao.Pontos,
            versao.Configuracao.Descrever(),
            CatalogoDeTiposDeRegra.Desmontar(versao.Configuracao),
            versao.PublicadaEm);
    }
}

/// <summary>
/// O rascunho aberto de uma regra.
///
/// <c>Valores</c> vem em numeros nomeados para que o formulario abra
/// preenchido; <c>Configuracao</c> vem em texto legivel para que a lista possa
/// mostrar o que mudaria sem montar o formulario.
/// </summary>
public sealed record RascunhoResposta(
    int Pontos,
    string Configuracao,
    IReadOnlyDictionary<string, decimal> Valores);

/// <summary>
/// Uma regra vista pela administracao.
///
/// <c>NoPerfilVigente</c> e diferente de <c>Ativa</c>: uma regra recem-criada
/// esta ativa e ainda nao vale para ninguem, porque nunca foi publicada. A
/// tela precisa dizer as duas coisas — senao o Supervisor acha que configurou
/// o motor quando so escreveu um rascunho.
/// </summary>
public sealed record RegraAdministradaResposta(
    Guid Id,
    string Tipo,
    string Nome,
    bool Ativa,
    int Versao,
    bool NoPerfilVigente,
    int? NumeroDaVersaoVigente,
    int? PontosVigentes,
    string? ConfiguracaoVigente,
    RascunhoResposta? Rascunho,
    IReadOnlyList<VersaoDeRegraResposta> Versoes)
{
    public static RegraAdministradaResposta De(RegraAdministrada item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var regra = item.Regra;

        var rascunho = regra.TemRascunho
            ? new RascunhoResposta(
                regra.PontosEmRascunho!.Value,
                regra.ConfiguracaoEmRascunho!.Descrever(),
                CatalogoDeTiposDeRegra.Desmontar(regra.ConfiguracaoEmRascunho))
            : null;

        return new RegraAdministradaResposta(
            regra.Id,
            regra.Tipo.ToString(),
            regra.Nome,
            regra.Ativa,
            regra.Versao,
            item.NoPerfilVigente,
            item.VersaoVigente?.Numero,
            item.VersaoVigente?.Pontos,
            item.VersaoVigente?.Configuracao.Descrever(),
            rascunho,
            [.. item.Versoes.Select(VersaoDeRegraResposta.De)]);
    }
}
