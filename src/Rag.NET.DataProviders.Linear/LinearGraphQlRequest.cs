using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Linear;

/// <summary>A GraphQL request envelope: the query document plus typed variables.</summary>
public sealed record LinearGraphQlRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("variables")] LinearIssueVariables Variables);
