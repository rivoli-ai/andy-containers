using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.Containers.Configurator;

/// <summary>
/// Serialization policy shared by writer + tests. Snake-case property names
/// match the AQ1 schema; nulls are skipped so optional fields like
/// <c>api_key_ref</c> or <c>boundaries</c> don't appear when unset (the
/// schema treats absent and null differently for some properties).
/// </summary>
public static class HeadlessConfigJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Serialize(HeadlessRunConfig config)
        => JsonSerializer.Serialize(config, Options);
}
