using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

public sealed class WeaviateProperty
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("dataType")]
    public required IReadOnlyList<string> DataType { get; init; }

    /// <summary>
    /// <c>"field"</c> keeps a text property as one token so <c>Equal</c> filters match the
    /// whole value (word tokenization would match per-token — fatal for document ids).
    /// </summary>
    [JsonPropertyName("tokenization")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tokenization { get; init; }
}
