using System.Text.Json;

namespace PaperBatch.Core.Serialization;

internal static class StrictJson
{
    public static void RejectDuplicateProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new JsonException($"Duplicate JSON property '{property.Name}'.");
                    }

                    RejectDuplicateProperties(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
                break;
        }
    }
}
