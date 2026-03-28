using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Jira;

public sealed class JiraOptions : CloudStorageOptions
{
    public required string BaseUrl  { get; init; }
    public required string Email    { get; init; }
    public string? ProjectKey       { get; init; }
    public string  Jql { get; init; } = "order by updated DESC";
}
