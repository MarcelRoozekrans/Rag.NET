using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Graph;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Exchange;

/// <summary>
/// Enumerates Exchange / Outlook mailbox messages as raw RFC 822 (<c>.eml</c>) documents via
/// the Microsoft Graph SDK using app-only authentication.
/// <para>
/// Each message becomes one <c>.eml</c> <see cref="FileHandle"/>; the MIME content is fetched
/// lazily from <c>/users/{mailbox}/messages/{id}/$value</c> only when the entry is ingested.
/// Ingesting the emitted entries requires a registered RFC 822 parser
/// (<c>AddEmailParser()</c> from <c>Rag.NET.Parsers.Email</c>) so attachments are dispatched
/// to the existing document parsers.
/// </para>
/// <para>
/// A delta run uses <see cref="CloudStorageOptions.DeltaToken"/> as a
/// <c>receivedDateTime ge</c> filter; <see cref="GetDeltaToken"/> returns a safe watermark
/// for the caller to persist — the max <c>receivedDateTime</c> seen on a full traversal,
/// the truncation point when <see cref="ExchangeMailOptions.MaxResults"/> fired in the last
/// folder, or <see langword="null"/> (keep the previous token) otherwise. Graph delta queries
/// (<c>/delta</c>) are intentionally not used — the date-range watermark plus the
/// hash-store skip covers incremental ingestion.
/// </para>
/// <para>
/// Watermark state (<see cref="GetDeltaToken"/>, <see cref="CompletedFullTraversal"/>) is
/// per-instance and not safe under concurrent <c>IngestFromProviderAsync</c> calls against
/// the same registration — the documented run-then-persist flow is assumed.
/// </para>
/// </summary>
public sealed class ExchangeMailDataProvider : FileContentProviderBase
{
    // Graph caps mail message pages at 1000 ($top); 100 keeps payloads small while
    // amortising paging round-trips.
    private const int PageSize = 100;

    private static readonly string[] SelectFields =
        ["id", "subject", "receivedDateTime", "lastModifiedDateTime", "hasAttachments"];

    private readonly GraphServiceClient  _graph;
    private readonly ExchangeMailOptions _options;
    private readonly ILogger<ExchangeMailDataProvider>? _logger;

    private DateTimeOffset? _maxReceived;          // global max across all folders (full-traversal token)
    private DateTimeOffset? _lastSeenInFolder;     // last-seen in the folder currently enumerating
    private DateTimeOffset? _truncationWatermark;  // safe token when the cap fired in the LAST folder

    public ExchangeMailDataProvider(
        GraphServiceClient graph,
        ExchangeMailOptions options,
        ILogger<ExchangeMailDataProvider>? logger = null)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph   = graph;
        _options = options;
        _logger  = logger;
    }

    /// <summary>
    /// <see langword="true"/> when the most recent enumeration visited every configured
    /// folder to completion — i.e. it neither hit <see cref="ExchangeMailOptions.MaxResults"/>
    /// nor stopped on a failure. This signals <b>completeness</b>, not watermark safety:
    /// a run truncated in the last folder still yields a safe partial-progress token from
    /// <see cref="GetDeltaToken"/> even though this property is <see langword="false"/>.
    /// </summary>
    public bool CompletedFullTraversal { get; private set; }

    /// <summary>
    /// Returns the watermark from the last enumeration in ISO-8601 round-trip format.
    /// Callers persist this value and pass it back via
    /// <see cref="CloudStorageOptions.DeltaToken"/> for incremental runs.
    /// <para>
    /// Full traversal → the maximum <c>receivedDateTime</c> seen across all folders.
    /// Truncated by <see cref="ExchangeMailOptions.MaxResults"/> while enumerating the
    /// <b>last</b> configured folder → that folder's last-seen <c>receivedDateTime</c>
    /// (provably safe: messages are ordered ascending, so everything unseen in that folder
    /// is newer, and all earlier folders completed) — backlogs larger than
    /// <c>MaxResults</c> therefore drain at <c>MaxResults</c> per run.
    /// Truncated in a non-last folder, failed, or no messages seen →
    /// <see langword="null"/>, which means <b>keep the previous token</b>; advancing it
    /// would permanently skip messages that were never enumerated.
    /// </para>
    /// <para>
    /// Persist the token only after an error-free run: the watermark advances during
    /// enumeration, before per-entry ingestion outcomes are known.
    /// Same-timestamp duplicates on the next run are caught by the hash-store skip.
    /// </para>
    /// </summary>
    public string? GetDeltaToken()
    {
        var watermark = CompletedFullTraversal ? _maxReceived : _truncationWatermark;
        return watermark?.ToString("o", CultureInfo.InvariantCulture);
    }

    protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _maxReceived           = null;
        _lastSeenInFolder      = null;
        _truncationWatermark   = null;
        CompletedFullTraversal = false;

        string? filter = null;
        if (_options.DeltaToken is not null)
        {
            if (!DateTimeOffset.TryParse(
                    _options.DeltaToken, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var since))
            {
                yield return Result<FileHandle, RagError>.Failure(new RagError.ValidationFailed(
                    [new ValidationFailure(
                        nameof(CloudStorageOptions.DeltaToken),
                        $"Invalid DeltaToken '{_options.DeltaToken}': expected an ISO-8601 receivedDateTime watermark.")]));
                yield break;
            }

            filter = string.Create(
                CultureInfo.InvariantCulture,
                $"receivedDateTime ge {since.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}");
        }

        IReadOnlyList<string> folders = _options.FolderIds is { Count: > 0 }
            ? _options.FolderIds
            : ["inbox"];

        var total = 0;
        for (int f = 0; f < folders.Count; f++)
        {
            _lastSeenInFolder = null;

            await foreach (var result in EnumerateFolderAsync(folders[f], filter, total, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return result;
                if (result.IsFailure)
                    yield break;

                if (++total >= _options.MaxResults)
                {
                    HandleTruncation(isLastFolder: f == folders.Count - 1);
                    yield break;
                }
            }
        }

        CompletedFullTraversal = true;
    }

    /// <summary>
    /// The cap fired. When it fired in the last configured folder, the folder's last-seen
    /// <c>receivedDateTime</c> is a safe watermark (ascending order: everything unseen in
    /// that folder is newer; all earlier folders completed) — backlogs then drain at
    /// <see cref="ExchangeMailOptions.MaxResults"/> per run. When it fired earlier, the
    /// watermark is withheld: later folders were never seen and advancing would skip their
    /// mail forever.
    /// </summary>
    private void HandleTruncation(bool isLastFolder)
    {
        if (isLastFolder && _lastSeenInFolder is { } lastSeen)
        {
            _truncationWatermark = lastSeen;
            if (_logger is not null)
            {
                ExchangeMailLog.EnumerationTruncatedWithProgress(
                    _logger, _options.Mailbox, _options.MaxResults,
                    lastSeen.ToString("o", CultureInfo.InvariantCulture));
            }
        }
        else if (_logger is not null)
        {
            ExchangeMailLog.EnumerationTruncatedWithheld(_logger, _options.Mailbox, _options.MaxResults);
        }
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> EnumerateFolderAsync(
        string folderId, string? filter, int emittedSoFar,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? nextLink = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageResult = await FetchPageAsync(folderId, filter, nextLink, cancellationToken)
                .ConfigureAwait(false);
            if (pageResult.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(pageResult.Error);
                yield break;
            }

            var page     = pageResult.Value;
            var messages = page.Value ?? [];
            for (int i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                if (string.IsNullOrEmpty(message.Id))
                    continue;

                if (message.ReceivedDateTime is { } received)
                {
                    // Last-seen in this folder (ascending $orderby) — the safe watermark
                    // when the cap fires in the last folder. Tracked separately from the
                    // global max: an earlier folder's max can exceed it.
                    _lastSeenInFolder = received;
                    if (_maxReceived is null || received > _maxReceived)
                        _maxReceived = received;
                }

                yield return Result<FileHandle, RagError>.Success(ToHandle(folderId, message));
                if (++emittedSoFar >= _options.MaxResults)
                    yield break;
            }

            nextLink = page.OdataNextLink;
        } while (nextLink is not null);
    }

    private async Task<Result<MessageCollectionResponse, RagError>> FetchPageAsync(
        string folderId, string? filter, string? nextLink, CancellationToken ct)
    {
        try
        {
            var builder = _graph.Users[_options.Mailbox].MailFolders[folderId].Messages;
            var page = nextLink is not null
                ? await builder.WithUrl(nextLink).GetAsync(cancellationToken: ct).ConfigureAwait(false)
                : await builder.GetAsync(rc =>
                    {
                        rc.QueryParameters.Select  = SelectFields;
                        rc.QueryParameters.Orderby = ["receivedDateTime asc"];
                        rc.QueryParameters.Top     = Math.Min(_options.MaxResults, PageSize);
                        if (filter is not null)
                            rc.QueryParameters.Filter = filter;
                    }, ct).ConfigureAwait(false);

            return page is not null
                ? Result<MessageCollectionResponse, RagError>.Success(page)
                // Synthetic status per the RagError.HttpFailed convention: the server answered,
                // but with no payload this connector can proceed from.
                : Result<MessageCollectionResponse, RagError>.Failure(new RagError.HttpFailed(
                    HttpStatusCode.NoContent, $"Graph returned an empty response for folder '{folderId}'."));
        }
        // GraphErrorMapping.IsMappable lets caller cancellation through untouched; everything
        // else it accepts becomes a Result failure rather than an escaping exception.
        catch (Exception ex) when (GraphErrorMapping.IsMappable(ex, ct))
        {
            return Result<MessageCollectionResponse, RagError>.Failure(GraphErrorMapping.Map(ex));
        }
    }

    /// <summary>
    /// Builds the handle for one message.
    /// </summary>
    /// <remarks>
    /// Phase 4.10 Task 5: <c>message.ReceivedDateTime</c> becomes
    /// <see cref="FileHandle.CreatedAt"/> — a deliberate modelling choice: a received mail's
    /// <c>receivedDateTime</c> is its "creation" for our purposes. <c>message.LastModifiedDateTime</c>
    /// becomes <see cref="FileHandle.UpdatedAt"/>, mirroring what already drives the ETag. The
    /// existing <c>received_at</c> metadata tag (built above) is kept exactly as-is.
    /// </remarks>
    private FileHandle ToHandle(string folderId, Message message)
    {
        var messageId = message.Id!;
        var fileName  = $"{FileNameSanitizer.Sanitize(message.Subject, $"message-{messageId}")}.eml";

        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["folder"]          = folderId,
            ["has_attachments"] = message.HasAttachments == true ? "true" : "false",
        };
        if (message.ReceivedDateTime is { } received)
            metadata["received_at"] = received.ToString("o", CultureInfo.InvariantCulture);

        return new FileHandle(
            Id:               $"{folderId}/{messageId}",
            FileName:         fileName,
            ETag:             message.LastModifiedDateTime?.ToString("o", CultureInfo.InvariantCulture),
            OpenContentAsync: ct => OpenMessageContentAsync(messageId, ct),
            Metadata:         metadata,
            CreatedAt:        message.ReceivedDateTime?.UtcDateTime,
            UpdatedAt:        message.LastModifiedDateTime?.UtcDateTime);
    }

    /// <summary>
    /// Lazily fetches the raw RFC 822 MIME stream from
    /// <c>/users/{mailbox}/messages/{id}/$value</c> — the Graph 5.x SDK exposes the
    /// <c>$value</c> segment as the <c>Content</c> request builder.
    /// </summary>
    private async Task<Stream> OpenMessageContentAsync(string messageId, CancellationToken ct)
    {
        var stream = await _graph.Users[_options.Mailbox].Messages[messageId].Content
            .GetAsync(cancellationToken: ct).ConfigureAwait(false);
        return stream ?? throw new InvalidOperationException(
            $"Graph returned no MIME content for message '{messageId}'.");
    }
}
