using System.Text.Json;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Identidade;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralAntifraude.Infrastructure.Persistencia.Configuracoes;

/// <summary>
/// Serializa e reconstroi o conteudo tipado de um evento.
///
/// Mesmo desenho da configuracao de regra, pela mesma razao: o tipo viaja
/// DENTRO do JSON porque o conversor do EF Core so enxerga a propria coluna, e
/// a leitura passa por um <c>switch</c> fechado — um tipo desconhecido falha
/// alto em vez de virar um objeto que ninguem sabe o que e.
///
/// Aqui a lista fechada tem um segundo papel: ela e o **contrato de evento**
/// (CLAUDE.md secao 41). Um evento novo, ou uma versao nova de um existente,
/// exige uma entrada nova aqui — e essa exigencia e o que impede uma mudanca
/// incompativel de passar despercebida.
/// </summary>
public static class SerializadorDeConteudoDeEvento
{
    private static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Serializar(ConteudoDeEvento conteudo)
    {
        ArgumentNullException.ThrowIfNull(conteudo);

        return JsonSerializer.Serialize(conteudo, conteudo.GetType(), Opcoes);
    }

    public static ConteudoDeEvento Desserializar(string tipo, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        ConteudoDeEvento? conteudo = tipo switch
        {
            TransacaoAvaliadaV1.NomeDoTipo =>
                JsonSerializer.Deserialize<TransacaoAvaliadaV1>(json, Opcoes),
            BacktestSolicitadoV1.NomeDoTipo =>
                JsonSerializer.Deserialize<BacktestSolicitadoV1>(json, Opcoes),
            _ => null,
        };

        return conteudo
            ?? throw new InvalidOperationException(
                $"Nao ha contrato conhecido para o tipo de evento '{tipo}'. " +
                "O catalogo de eventos e fechado e versionado.");
    }
}

public sealed class ConfiguracaoDeEventoDeSaida : IEntityTypeConfiguration<EventoDeSaida>
{
    public void Configure(EntityTypeBuilder<EventoDeSaida> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("eventos_de_saida");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrganizacaoId).IsRequired();
        builder.Property(e => e.OcorridoEm).IsRequired();
        builder.Property(e => e.TentativasDePublicacao).IsRequired();

        builder.Property(e => e.Tipo)
            .HasMaxLength(EventoDeSaida.TamanhoMaximoDoTipo)
            .IsRequired();

        builder.Property(e => e.IdDeCorrelacao)
            .HasMaxLength(EventoDeSaida.TamanhoMaximoDaCorrelacao)
            .IsRequired();

        // O conteudo depende da coluna `tipo` para ser lido de volta, e o
        // conversor nao pode consultar coluna vizinha. Por isso o tipo e
        // passado ao desserializador a partir do proprio JSON, que o carrega:
        // o `$type` do System.Text.Json ficaria implicito demais, entao o
        // nome do tipo e escrito e lido explicitamente.
        builder.Property(e => e.Conteudo)
            .HasConversion(
                conteudo => SerializadorDeConteudoDeEvento.Serializar(conteudo),
                json => SerializadorDeConteudoDeEvento.Desserializar(LerTipo(json), json))
            .HasColumnType("jsonb")
            .IsRequired();

        // A consulta do despachante da Fase 5: as pendentes, mais antigas
        // primeiro. O indice e parcial — as publicadas nunca sao lidas por
        // este caminho, e mante-las fora do indice o deixa pequeno mesmo
        // quando a tabela cresce.
        builder.HasIndex(e => new { e.PublicadoEm, e.OcorridoEm })
            .HasDatabaseName("ix_eventos_de_saida_pendentes")
            .HasFilter("publicado_em IS NULL");

        builder.HasOne<Organizacao>()
            .WithMany()
            .HasForeignKey(e => e.OrganizacaoId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    /// <summary>
    /// Le o nome do tipo de dentro do proprio JSON.
    ///
    /// O <c>ConteudoDeEvento</c> expoe <c>Tipo</c> como propriedade, e o
    /// serializador a grava junto do resto. Ler dali evita depender da coluna
    /// vizinha, que o conversor do EF nao alcanca.
    /// </summary>
    private static string LerTipo(string json)
    {
        using var documento = JsonDocument.Parse(json);

        return documento.RootElement.TryGetProperty("tipo", out var tipo)
            ? tipo.GetString() ?? string.Empty
            : string.Empty;
    }
}
