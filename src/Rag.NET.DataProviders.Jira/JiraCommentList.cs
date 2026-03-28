using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

internal sealed record JiraCommentList(
    [property: JsonPropertyName("comments")] List<JiraComment> Comments);
