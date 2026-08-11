using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Gmail;

/// <summary>Configuration for <see cref="GmailDataProvider"/>.</summary>
public sealed class GmailOptions : CloudStorageOptions
{
    /// <summary>Gmail user name (email address) used for IMAP OAuth2 authentication.</summary>
    public string UserName   { get; set; } = string.Empty;

    /// <summary>
    /// A Gmail search expression — the same syntax as the Gmail search box, e.g.
    /// <c>"from:alice@example.com has:attachment"</c> or <c>"newer_than:7d"</c> — narrowing which
    /// messages are enumerated. Empty (the default) enumerates the whole mailbox.
    /// <para>
    /// Applied server-side through Gmail's <c>X-GM-RAW</c> IMAP extension, so the expression is
    /// evaluated by Gmail itself rather than reinterpreted here; anything the Gmail UI accepts
    /// works. A server that does not advertise the extension makes the provider <b>throw</b>
    /// rather than enumerate unfiltered — a query that silently matches everything is the failure
    /// this setting is being fixed out of.
    /// </para>
    /// <para>
    /// The default was <c>"in:inbox"</c> while nothing read this property, which stated a scoping
    /// that no code performed (issue #108). It is empty now: the provider opens the Inbox and only
    /// the Inbox, so inbox scoping is a property of where it looks, not of this string.
    /// </para>
    /// </summary>
    public string Query      { get; set; } = string.Empty;

    /// <summary>Maximum number of messages to retrieve per enumeration. Defaults to 500.</summary>
    public int    MaxResults { get; set; } = 500;
}
