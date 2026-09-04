using System.Text.Json;
using System.Text.Json.Nodes;
using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Infrastructure.Persistencia.Configuracoes;

namespace CentralAntifraude.Infrastructure.Mensageria;

/// <summary>Uma mensagem que nao pode ser interpretada. Nao adianta repetir.</summary>
public sealed class EnvelopeInvalido : Exception
{
    public EnvelopeInvalido(string mensagem)
        : base(mensagem)
    {
    }

    public EnvelopeInvalido(string mensagem, Exception causa)
        : base(mensagem, causa)
    {
    }
}

/// <summary>
/// O formato de fio do envelope.
///
/// **Este e o contrato publico da mensageria.** Quando a Fase 14 trocar a fila
/// local pela SQS, e este JSON que vai no corpo da mensagem — sem mudanca. Por
/// isso os nomes dos campos sao os do ROADMAP secao 5.3, e nao os nomes das
/// propriedades C#.
///
/// A leitura e defensiva por natureza: o que chega da fila e **dado**, nunca
/// instrucao. Campo faltando, tipo desconhecido, versao que este processo nao
/// entende, JSON malformado — tudo vira <see cref="EnvelopeInvalido"/>, que o
/// worker trata como mensagem envenenada em vez de repetir para sempre.
/// </summary>
public static class SerializadorDeEnvelope
{
    public const string CampoEventId = "eventId";
    public const string CampoEventType = "eventType";
    public const string CampoVersion = "version";
    public const string CampoTenantId = "tenantId";
    public const string CampoCorrelationId = "correlationId";
    public const string CampoOccurredAt = "occurredAt";
    public const string CampoPayload = "payload";

    private static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Serializar(EnvelopeDeEvento envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var corpo = new JsonObject
        {
            [CampoEventId] = envelope.EventId.ToString(),
            [CampoEventType] = envelope.EventType,
            [CampoVersion] = envelope.Version,
            [CampoTenantId] = envelope.TenantId.ToString(),
            [CampoCorrelationId] = envelope.CorrelationId,
            [CampoOccurredAt] = envelope.OccurredAt.ToString("O"),
            [CampoPayload] = JsonNode.Parse(
                SerializadorDeConteudoDeEvento.Serializar(envelope.Conteudo)),
        };

        return corpo.ToJsonString(Opcoes);
    }

    public static EnvelopeDeEvento Desserializar(string corpo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpo);

        JsonDocument documento;

        try
        {
            documento = JsonDocument.Parse(corpo);
        }
        catch (JsonException excecao)
        {
            throw new EnvelopeInvalido("A mensagem nao e um JSON valido.", excecao);
        }

        using (documento)
        {
            var raiz = documento.RootElement;

            if (raiz.ValueKind != JsonValueKind.Object)
            {
                throw new EnvelopeInvalido("A mensagem nao e um objeto JSON.");
            }

            var eventId = LerGuid(raiz, CampoEventId);
            var eventType = LerTexto(raiz, CampoEventType);
            var version = LerInteiro(raiz, CampoVersion);
            var tenantId = LerGuid(raiz, CampoTenantId);
            var correlationId = LerTextoOpcional(raiz, CampoCorrelationId);
            var occurredAt = LerInstante(raiz, CampoOccurredAt);

            if (!raiz.TryGetProperty(CampoPayload, out var payload) ||
                payload.ValueKind != JsonValueKind.Object)
            {
                throw new EnvelopeInvalido($"Campo '{CampoPayload}' ausente ou nao e um objeto.");
            }

            try
            {
                var conteudo = SerializadorDeConteudoDeEvento.Desserializar(
                    EnvelopeDeEvento.Compor(eventType, version),
                    payload.GetRawText());

                return EnvelopeDeEvento.Reconstruir(
                    eventId,
                    eventType,
                    version,
                    tenantId,
                    correlationId,
                    occurredAt,
                    conteudo);
            }
            catch (Exception excecao) when (excecao is InvalidOperationException or JsonException or ViolacaoDeInvariante)
            {
                // Tipo desconhecido, versao que este processo nao entende, ou
                // payload que nao casa com o que o envelope declara. Nos tres
                // casos, repetir daria o mesmo resultado.
                throw new EnvelopeInvalido(
                    $"Nao foi possivel interpretar o evento '{EnvelopeDeEvento.Compor(eventType, version)}'.",
                    excecao);
            }
        }
    }

    private static string LerTexto(JsonElement raiz, string campo) =>
        raiz.TryGetProperty(campo, out var valor) && valor.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(valor.GetString())
            ? valor.GetString()!
            : throw new EnvelopeInvalido($"Campo '{campo}' ausente ou vazio.");

    private static string LerTextoOpcional(JsonElement raiz, string campo) =>
        raiz.TryGetProperty(campo, out var valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString() ?? string.Empty
            : string.Empty;

    private static Guid LerGuid(JsonElement raiz, string campo) =>
        Guid.TryParse(LerTexto(raiz, campo), out var guid) && guid != Guid.Empty
            ? guid
            : throw new EnvelopeInvalido($"Campo '{campo}' nao e um identificador valido.");

    private static int LerInteiro(JsonElement raiz, string campo) =>
        raiz.TryGetProperty(campo, out var valor) &&
        valor.ValueKind == JsonValueKind.Number &&
        valor.TryGetInt32(out var numero) &&
        numero >= 1
            ? numero
            : throw new EnvelopeInvalido($"Campo '{campo}' ausente ou nao e um inteiro positivo.");

    private static DateTimeOffset LerInstante(JsonElement raiz, string campo) =>
        DateTimeOffset.TryParse(
            LerTexto(raiz, campo),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var instante)
            ? instante
            : throw new EnvelopeInvalido($"Campo '{campo}' nao e um instante valido.");
}
