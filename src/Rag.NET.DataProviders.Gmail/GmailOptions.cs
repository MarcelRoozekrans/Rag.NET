using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Gmail;

/// <summary>Configuration for <see cref="GmailDataProvider"/>.</summary>
public sealed class GmailOptions : CloudStorageOptions
{
    /// <summary>Gmail user name (email address) used for IMAP OAuth2 authentication.</summary>
    public string UserName   { get; init; } = string.Empty;

    /// <summary>
    /// Reserved for a future implementation that will translate this value into an
    /// IMAP <c>SEARCH</c> query. Currently unused by the provider.
    /// </summary>
    public string Query      { get; init; } = "in:inbox";

    /// <summary>Maximum number of messages to retrieve per enumeration. Defaults to 500.</summary>
    public int    MaxResults { get; init; } = 500;
}
