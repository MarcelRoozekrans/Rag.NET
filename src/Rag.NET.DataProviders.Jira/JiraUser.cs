using System.Text.Json.Serialization;

namespace Rag.NET.DataProviders.Jira;

internal sealed record JiraUser(
    [property: JsonPropertyName("displayName")] string DisplayName);
