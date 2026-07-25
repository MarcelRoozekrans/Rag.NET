using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>Linear's nested team filter: <c>team: { key: { in: [...] } }</c>.</summary>
public sealed record LinearTeamFilter(
    [property: JsonPropertyName("key")] LinearStringInComparator Key);
