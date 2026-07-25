using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Exchange;

/// <summary>Configuration for <see cref="ExchangeMailDataProvider"/>.</summary>
public sealed class ExchangeMailOptions : CloudStorageOptions
{
    /// <summary>
    /// Mailbox UPN (e.g. <c>"ingest@contoso.com"</c>) whose messages are enumerated.
    /// Required — validated at registration by
    /// <c>AddExchangeMailDataProvider</c>.
    /// </summary>
    public string Mailbox { get; set; } = string.Empty;

    /// <summary>
    /// Mail folder ids or Graph well-known folder names (e.g. <c>"inbox"</c>,
    /// <c>"archive"</c>, <c>"sentitems"</c>) to enumerate.
    /// When <see langword="null"/> or empty, only the Inbox is enumerated.
    /// </summary>
    public IReadOnlyList<string>? FolderIds { get; set; }

    /// <summary>Maximum number of messages per enumeration (across all folders). Defaults to 500.</summary>
    public int MaxResults { get; set; } = 500;
}
