using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>Linear's date comparator restricted to the <c>gt</c> operator (delta watermark).</summary>
public sealed record LinearDateComparator(
    [property: JsonPropertyName("gt")] DateTimeOffset Gt);
