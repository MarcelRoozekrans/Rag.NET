using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

public sealed record JiraCommentList(
    [property: JsonPropertyName("comments")] IReadOnlyList<JiraComment> Comments);
