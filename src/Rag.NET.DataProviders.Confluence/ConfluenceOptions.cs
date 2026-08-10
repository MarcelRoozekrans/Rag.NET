using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Confluence;

/// <summary>Configuration for <see cref="ConfluenceDataProvider"/>.</summary>
public sealed class ConfluenceOptions : CloudStorageOptions
{
    /// <summary>
    /// Base URL of the Confluence instance (e.g. <c>https://mysite.atlassian.net/wiki</c>).
    /// Read after the <c>configure</c> callback runs, so a callback that changes it changes the
    /// HttpClient's base address.
    /// </summary>
    public required string BaseUrl { get; set; }

    /// <summary>
    /// Email address used for Basic authentication together with the API token.
    /// Read after the <c>configure</c> callback runs, so a callback that changes it changes the
    /// credentials sent.
    /// </summary>
    public required string Email   { get; set; }

    /// <summary>
    /// Optional Confluence space key to restrict results.
    /// Must match the pattern <c>^[A-Za-z0-9\-_]+$</c>.
    /// When <see langword="null"/>, all accessible pages are returned.
    /// </summary>
    public string? SpaceKey        { get; init; }
}
