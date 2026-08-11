using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Zendesk;

/// <summary>Configuration for <see cref="ZendeskArticlesDataProvider"/>.</summary>
public sealed class ZendeskArticlesOptions : CloudStorageOptions
{
    /// <summary>Zendesk subdomain (e.g. <c>"mycompany"</c> → <c>https://mycompany.zendesk.com</c>).</summary>
    public required string Subdomain { get; set; }

    /// <summary>
    /// Agent email address used for Basic authentication.
    /// Read after the <c>configure</c> callback runs, so a callback that changes it changes the
    /// credentials sent.
    /// </summary>
    public required string Email { get; set; }
}
