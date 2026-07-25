using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>
/// Typed variables for the issues query. <c>null</c> members are omitted from the JSON
/// (a serialized <c>"filter": null</c> is valid GraphQL, but omission keeps the payload
/// identical to a query without the variable).
/// </summary>
public sealed record LinearIssueVariables(
    [property: JsonPropertyName("first")] int First,
    [property: JsonPropertyName("after"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? After,
    [property: JsonPropertyName("filter"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LinearIssueFilter? Filter);
