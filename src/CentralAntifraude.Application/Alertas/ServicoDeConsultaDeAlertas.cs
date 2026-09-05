using CentralAntifraude.Application.Comum;
using CentralAntifraude.Application.Risco;

namespace CentralAntifraude.Application.Alertas;

/// <summary>
/// A fila de trabalho do analista.
///
/// **Duas consultas por pagina, nunca uma por linha.** O alerta guarda score e
/// decisao, entao filtrar e ordenar nao precisa de juncao; os sinais vem de
/// uma segunda consulta que cobre a pagina inteira. Um <c>Include</c> traria
/// os sinais multiplicando as linhas do alerta, e uma consulta por alerta
/// seria N+1 na tela mais usada do produto (`CLAUDE.md` secao 99).
/// </summary>
public sealed class ServicoDeConsultaDeAlertas
{
    /// <summary>
    /// Campos pelos quais a fila pode ser ordenada.
    ///
    /// Lista fechada: o campo de ordenacao nunca vem livre do cliente.
    /// </summary>
    public static readonly string[] CamposDeOrdenacao = ["criadoEm", "score", "prioridade"];

    /// <summary>
    /// Ordem padrao: o alerta mais recente primeiro.
    ///
    /// Nao por prioridade. Ordenar por prioridade como padrao deixaria os
    /// `Revisar` do dia inteiro atras dos `Bloquear` da semana passada, e a
    /// fila pararia de refletir o que esta acontecendo agora. Quem quer
    /// trabalhar por gravidade pede a ordenacao por prioridade — e ela existe.
    /// </summary>
    public const string OrdenacaoPadrao = "criadoEm";

    private readonly IRepositorioDeAlertas _alertas;
    private readonly IRepositorioDeRisco _risco;

    public ServicoDeConsultaDeAlertas(IRepositorioDeAlertas alertas, IRepositorioDeRisco risco)
    {
        _alertas = alertas;
        _risco = risco;
    }

    public async Task<Pagina<AlertaNaFila>> ListarAsync(
        FiltroDeAlertas filtro,
        ParametrosDePaginacao paginacao,
        ParametrosDeOrdenacao ordenacao,
        CancellationToken cancellationToken)
    {
        var pagina = await _alertas.ListarAsync(filtro, paginacao, ordenacao, cancellationToken);

        var sinais = await _risco.BuscarSinaisPorAvaliacoesAsync(
            pagina.Itens.Select(a => a.AvaliacaoId).ToList(),
            cancellationToken);

        var itens = pagina.Itens
            .Select(alerta => new AlertaNaFila(
                alerta,
                sinais.TryGetValue(alerta.AvaliacaoId, out var doAlerta) ? doAlerta : []))
            .ToList();

        return new Pagina<AlertaNaFila>(itens, paginacao, pagina.TotalDeItens);
    }
}
