using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>Linear's string comparator restricted to the <c>in</c> operator.</summary>
public sealed record LinearStringInComparator(
    [property: JsonPropertyName("in")] IReadOnlyList<string> In);
