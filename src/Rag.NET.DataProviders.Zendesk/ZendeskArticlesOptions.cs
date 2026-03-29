using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Zendesk;

/// <summary>Configuration for a future Zendesk articles data provider.</summary>
public sealed class ZendeskArticlesOptions : CloudStorageOptions
{
    /// <summary>Zendesk subdomain (e.g. <c>"mycompany"</c> → <c>https://mycompany.zendesk.com</c>).</summary>
    public required string Subdomain { get; init; }

    /// <summary>Agent email address used for Basic authentication.</summary>
    public required string Email { get; init; }
}
