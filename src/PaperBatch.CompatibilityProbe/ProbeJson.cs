using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperBatch.CompatibilityProbe;

public static class ProbeJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new EvidenceStatusJsonConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    private sealed class EvidenceStatusJsonConverter : JsonConverter<EvidenceStatus>
    {
        public override EvidenceStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() switch
            {
                "verified" => EvidenceStatus.Verified,
                "unavailable" => EvidenceStatus.Unavailable,
                "blocked" => EvidenceStatus.Blocked,
                "not-run" => EvidenceStatus.NotRun,
                _ => throw new JsonException("Unknown probe evidence status.")
            };

        public override void Write(Utf8JsonWriter writer, EvidenceStatus value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value switch
            {
                EvidenceStatus.Verified => "verified",
                EvidenceStatus.Unavailable => "unavailable",
                EvidenceStatus.Blocked => "blocked",
                EvidenceStatus.NotRun => "not-run",
                _ => throw new JsonException("Unknown probe evidence status.")
            });
        }
    }
}
