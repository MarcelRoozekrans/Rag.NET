using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>Linear's workflow-state filter: <c>state: { type: { in: [...] } }</c>.</summary>
public sealed record LinearStateFilter(
    [property: JsonPropertyName("type")] LinearStringInComparator Type);
