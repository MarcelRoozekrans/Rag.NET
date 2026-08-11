using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Jira;

/// <summary>Configuration for <see cref="JiraDataProvider"/>.</summary>
public sealed class JiraOptions : CloudStorageOptions
{
    /// <summary>
    /// Base URL of the Jira instance (e.g. <c>https://mysite.atlassian.net</c>).
    /// Read after the <c>configure</c> callback runs, so a callback that changes it changes the
    /// HttpClient's base address.
    /// </summary>
    public required string BaseUrl  { get; set; }

    /// <summary>
    /// Email address used for Basic authentication together with the API token.
    /// Read after the <c>configure</c> callback runs, so a callback that changes it changes the
    /// credentials sent.
    /// </summary>
    public required string Email    { get; set; }

    /// <summary>
    /// Optional Jira project key to restrict results (e.g. <c>PROJ</c>).
    /// Must match the pattern <c>^[A-Za-z0-9\-_]+$</c>.
    /// When <see langword="null"/>, issues across all accessible projects are returned.
    /// </summary>
    public string? ProjectKey       { get; set; }

    /// <summary>
    /// Base JQL clause appended to every query. Defaults to <c>order by updated DESC</c>.
    /// </summary>
    public string  Jql { get; set; } = "order by updated DESC";
}
