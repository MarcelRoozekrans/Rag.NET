using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

/// <summary>REST-side <c>where</c> operand (the GraphQL side inlines its filter text).</summary>
public sealed class WeaviateWhereFilter
{
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Path { get; init; }

    [JsonPropertyName("operator")]
    public required string Operator { get; init; }

    [JsonPropertyName("valueText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueText { get; init; }

    [JsonPropertyName("operands")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<WeaviateWhereFilter>? Operands { get; init; }
}
