using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CentralAntifraude.Domain.Backtests;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Risco;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralAntifraude.Infrastructure.Persistencia.Configuracoes;

/// <summary>
/// Grava e le o snapshot do perfil candidato e o resultado da apuracao.
///
/// **O perfil candidato precisa de serializador proprio** porque carrega
/// <see cref="ConfiguracaoDeRegra"/>, que e polimorfica: o <c>switch</c>
/// fechado de <see cref="SerializadorDeConfiguracaoDeRegra"/> continua sendo a
/// unica porta pela qual uma configuracao volta do banco. Deixar o
/// <c>System.Text.Json</c> resolver o tipo sozinho abriria exatamente a porta
/// que o catalogo fechado existe para manter trancada (CLAUDE.md secao 21).
///
/// O resultado, ao contrario, e um documento so de numeros e nomes de enum —
/// nada ali vira comportamento.
/// </summary>
public static class SerializadorDoBacktest
{
    private const string CampoConfiguracao = "configuracao";

    private static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string SerializarCandidato(PerfilCandidato candidato)
    {
        ArgumentNullException.ThrowIfNull(candidato);

        var regras = new JsonArray();

        foreach (var regra in candidato.Regras)
        {
            regras.Add(new JsonObject
            {
                ["regraId"] = regra.RegraId.ToString(),
                ["nome"] = regra.Nome,
                ["tipo"] = regra.Tipo.ToString(),
                ["pontos"] = regra.Pontos,
                ["origem"] = regra.Origem.ToString(),
                [CampoConfiguracao] = JsonNode.Parse(
                    SerializadorDeConfiguracaoDeRegra.Serializar(regra.Configuracao)),
            });
        }

        return new JsonObject
        {
            ["limiarDeRevisao"] = candidato.LimiarDeRevisao,
            ["limiarDeBloqueio"] = candidato.LimiarDeBloqueio,
            ["regras"] = regras,
        }.ToJsonString(Opcoes);
    }

    public static PerfilCandidato DesserializarCandidato(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var documento = JsonDocument.Parse(json);
        var raiz = documento.RootElement;

        var regras = new List<RegraCandidata>();

        foreach (var elemento in raiz.GetProperty("regras").EnumerateArray())
        {
            regras.Add(new RegraCandidata(
                elemento.GetProperty("regraId").GetGuid(),
                elemento.GetProperty("nome").GetString() ?? string.Empty,
                LerEnum<TipoDeRegra>(elemento, "tipo"),
                SerializadorDeConfiguracaoDeRegra.Desserializar(
                    elemento.GetProperty(CampoConfiguracao).GetRawText()),
                elemento.GetProperty("pontos").GetInt32(),
                LerEnum<OrigemDaRegraCandidata>(elemento, "origem")));
        }

        return new PerfilCandidato(
            raiz.GetProperty("limiarDeRevisao").GetInt32(),
            raiz.GetProperty("limiarDeBloqueio").GetInt32(),
            regras);
    }

    public static string SerializarResultado(ResultadoDoBacktest resultado) =>
        JsonSerializer.Serialize(resultado, Opcoes);

    public static ResultadoDoBacktest DesserializarResultado(string json) =>
        JsonSerializer.Deserialize<ResultadoDoBacktest>(json, Opcoes)
        ?? throw new InvalidOperationException("Resultado de backtest ilegivel.");

    /// <summary>
    /// Le um enum pelo NOME, e nunca pelo numero.
    ///
    /// Mesma disciplina do vocabulario fechado das rotas: o inteiro muda de
    /// significado se alguem reordenar o enum, e um valor fora da lista
    /// precisa falhar alto em vez de virar um membro que nao existe.
    /// </summary>
    private static T LerEnum<T>(JsonElement elemento, string campo)
        where T : struct, Enum
    {
        var texto = elemento.GetProperty(campo).GetString();

        var canonico = Enum.GetNames<T>().FirstOrDefault(
            nome => string.Equals(nome, texto, StringComparison.Ordinal));

        return canonico is not null
            ? Enum.Parse<T>(canonico)
            : throw new InvalidOperationException(
                $"Valor '{texto}' fora do vocabulario de {typeof(T).Name}.");
    }
}

public sealed class ConfiguracaoDeExecucaoDeBacktest : IEntityTypeConfiguration<ExecucaoDeBacktest>
{
    public void Configure(EntityTypeBuilder<ExecucaoDeBacktest> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("execucoes_de_backtest");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrganizacaoId).IsRequired();
        builder.Property(e => e.VersaoDePerfilVigenteId).IsRequired();
        builder.Property(e => e.NumeroDaVersaoDePerfilVigente).IsRequired();
        builder.Property(e => e.Inicio).IsRequired();
        builder.Property(e => e.Fim).IsRequired();
        builder.Property(e => e.SolicitadaPorId).IsRequired();
        builder.Property(e => e.SolicitadaEm).IsRequired();

        builder.Property(e => e.Descricao)
            .HasMaxLength(ExecucaoDeBacktest.TamanhoMaximoDaDescricao)
            .IsRequired();

        builder.Property(e => e.SolicitadaPorDescricao)
            .HasMaxLength(ExecucaoDeBacktest.TamanhoMaximoDaDescricao)
            .IsRequired();

        builder.Property(e => e.MensagemDeErro)
            .HasMaxLength(ExecucaoDeBacktest.TamanhoMaximoDoErro);

        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(e => e.Candidato)
            .HasConversion(
                candidato => SerializadorDoBacktest.SerializarCandidato(candidato),
                json => SerializadorDoBacktest.DesserializarCandidato(json),
                ComparadorPorJson(
                    SerializadorDoBacktest.SerializarCandidato,
                    SerializadorDoBacktest.DesserializarCandidato))
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(e => e.Resultado)
            .HasConversion(
                resultado => SerializadorDoBacktest.SerializarResultado(resultado!),
                json => SerializadorDoBacktest.DesserializarResultado(json))
            .HasColumnType("jsonb");

        // Token de concorrencia. Faz dois trabalhos: impede que dois workers
        // concluam a mesma execucao e arbitra o cancelamento durante a
        // execucao, sem nenhuma consulta de verificacao no meio do laco.
        builder.Property(e => e.Versao).IsRequired().IsConcurrencyToken();

        // A tela lista as execucoes do tenant, da mais recente para a mais
        // antiga. E a unica consulta de listagem que existe.
        builder.HasIndex(e => new { e.OrganizacaoId, e.SolicitadaEm });

        // Contagem de execucoes em andamento, que e o limite contra spam.
        builder.HasIndex(e => new { e.OrganizacaoId, e.Status });

        builder.HasOne<Organizacao>()
            .WithMany()
            .HasForeignKey(e => e.OrganizacaoId)
            .OnDelete(DeleteBehavior.Restrict);

        // A versao de perfil usada como comparacao e amarrada no banco, com
        // Restrict: um resultado de backtest que apontasse para uma versao
        // apagada nao poderia mais ser explicado — o mesmo raciocinio da
        // avaliacao de risco.
        builder.HasOne<VersaoDePerfilDeRisco>()
            .WithMany()
            .HasForeignKey(e => e.VersaoDePerfilVigenteId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    /// <summary>
    /// Comparador explicito para os documentos jsonb.
    ///
    /// Os dois sao records, mas carregam listas: a igualdade sintetizada do
    /// record compara as listas por REFERENCIA, entao o EF nao detectaria
    /// mudanca nem saberia clonar o valor original. Comparar pelo JSON
    /// serializado e o mesmo caminho ja usado na evidencia de um sinal.
    /// </summary>
    private static ValueComparer<T> ComparadorPorJson<T>(
        Func<T, string> serializar,
        Func<string, T> desserializar)
        where T : class =>
        new(
            (esquerda, direita) => serializar(esquerda!) == serializar(direita!),
            valor => serializar(valor).GetHashCode(StringComparison.Ordinal),
            valor => desserializar(serializar(valor)));
}
