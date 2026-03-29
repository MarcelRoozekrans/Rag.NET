using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Rag.NET.DataProviders;

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

    protected override async IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
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

        var inbox = client.Inbox;
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
            yield return ToHandle(uid, message);
        }

        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private static FileHandle ToHandle(UniqueId uid, MimeMessage message)
    {
        var markdown = ToMarkdown(message);
        var subject  = string.IsNullOrWhiteSpace(message.Subject)
            ? $"message-{uid}" : message.Subject;

        // Replace invalid filename chars with underscore
        var invalid = Path.GetInvalidFileNameChars();
        var safe    = new char[subject.Length];
        for (int i = 0; i < subject.Length; i++)
            safe[i] = Array.IndexOf(invalid, subject[i]) >= 0 ? '_' : subject[i];

        return new FileHandle(
            Id:               uid.ToString(),
            FileName:         $"{new string(safe)}.md",
            ETag:             uid.ToString(),
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
    }

    private static string ToMarkdown(MimeMessage message)
    {
        var body = message.TextBody
            ?? HtmlTagRegex().Replace(message.HtmlBody ?? string.Empty, string.Empty);

        return $"# {message.Subject}\n\n**From:** {message.From}  **Date:** {message.Date:R}  **To:** {message.To}\n\n{body.Trim()}";
    }
}
