using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Gmail;

/// <summary>
/// Enumerates Gmail messages as Markdown documents via IMAP using MailKit.
/// <para>
/// Authentication uses OAuth2 with a token obtained from the registered
/// <see cref="ITokenProvider"/>. A delta run uses <see cref="GmailOptions.DeltaToken"/>
/// as a <see cref="MailKit.UniqueId"/> watermark, fetching only messages with a higher UID.
/// </para>
/// <para>
/// The plain-text body is preferred; when unavailable the HTML body is stripped of tags.
/// </para>
/// </summary>
public sealed partial class GmailDataProvider : FileContentProviderBase
{
    private readonly ITokenProvider    _tokenProvider;
    private readonly GmailOptions      _options;
    private readonly Func<IImapClient> _clientFactory;

    [GeneratedRegex("<[^>]+>", RegexOptions.NonBacktracking)]
    private static partial Regex HtmlTagRegex();

    public GmailDataProvider(
        ITokenProvider tokenProvider,
        GmailOptions options,
        Func<IImapClient>? clientFactory = null)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
        _options       = options;
        _clientFactory = clientFactory ?? (() => new ImapClient());
    }

    protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory();

        await client.ConnectAsync(
            "imap.gmail.com", 993,
            MailKit.Security.SecureSocketOptions.SslOnConnect,
            cancellationToken).ConfigureAwait(false);

        await client.AuthenticateAsync(
            new MailKit.Security.SaslMechanismOAuth2(_options.UserName, token),
            cancellationToken).ConfigureAwait(false);

        var inbox = client.Inbox ?? throw new InvalidOperationException("IMAP client has no Inbox folder.");
        await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

        IList<UniqueId> uids;
        if (_options.DeltaToken is not null
            && UniqueId.TryParse(_options.DeltaToken, out var lastUid))
        {
            uids = await inbox.SearchAsync(
                SearchQuery.Uids(new UniqueIdRange(
                    new UniqueId(lastUid.Id + 1), UniqueId.MaxValue)),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            uids = await inbox.SearchAsync(
                SearchQuery.All,
                cancellationToken).ConfigureAwait(false);
        }

        var limit = Math.Min(uids.Count, _options.MaxResults);
        for (int i = 0; i < limit; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uid     = uids[i];
            var message = await inbox.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
            yield return Result<FileHandle, RagError>.Success(ToHandle(uid, message));
        }

        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private static FileHandle ToHandle(UniqueId uid, MimeMessage message)
    {
        var markdown = ToMarkdown(message);
        var stem     = FileNameSanitizer.Sanitize(message.Subject, $"message-{uid}");

        return new FileHandle(
            Id:               uid.ToString(),
            FileName:         $"{stem}.md",
            ETag:             uid.ToString(),
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))),
            Metadata:         BuildMetadata(message),
            CreatedAt:        message.Date.UtcDateTime);
    }

    /// <summary>
    /// The message's filterable fields. Sender and date are <i>also</i> rendered into the
    /// Markdown header by <see cref="ToMarkdown"/>: the body drives semantic recall, the tags
    /// drive filtering, and neither substitutes for the other.
    /// <para>
    /// <c>date</c> is round-trip ISO-8601 rather than the header's RFC 822 rendering, so tag
    /// values sort and compare consistently across connectors.
    /// </para>
    /// <para>
    /// Phase 4.10 Task 5: <c>message.Date</c> also becomes the typed
    /// <see cref="FileHandle.CreatedAt"/> (see <see cref="ToHandle"/>) — a received/sent email's
    /// <c>Date</c> header is its creation for our purposes. This <c>date</c> tag is kept exactly
    /// as-is; the typed field is an addition, not a replacement.
    /// </para>
    /// </summary>
    private static Dictionary<string, MetadataValue> BuildMetadata(MimeMessage message)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["date"]            = message.Date.ToString("o", CultureInfo.InvariantCulture),
            ["has_attachments"] = message.Attachments.Any() ? "true" : "false",
        };

        var from = message.From.ToString();
        if (!string.IsNullOrEmpty(from)) metadata["from"] = from;
        return metadata;
    }

    private static string ToMarkdown(MimeMessage message)
    {
        var body = message.TextBody
            ?? HtmlTagRegex().Replace(message.HtmlBody ?? string.Empty, string.Empty);

        return $"# {message.Subject}\n\n**From:** {message.From}  **Date:** {message.Date:R}  **To:** {message.To}\n\n{body.Trim()}";
    }
}
