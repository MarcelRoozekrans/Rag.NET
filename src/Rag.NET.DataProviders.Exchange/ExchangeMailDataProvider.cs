using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Rag.NET.DataProviders;
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
/// <c>receivedDateTime ge</c> filter; <see cref="GetDeltaToken"/> returns the max
/// <c>receivedDateTime</c> seen for the caller to persist. Graph delta queries
/// (<c>/delta</c>) are intentionally not used — the date-range watermark plus the
/// hash-store skip covers incremental ingestion.
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

    private DateTimeOffset? _maxReceived;

    public ExchangeMailDataProvider(GraphServiceClient graph, ExchangeMailOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph   = graph;
        _options = options;
    }

    /// <summary>
    /// Returns the watermark from the last completed traversal — the maximum
    /// <c>receivedDateTime</c> seen, in ISO-8601 round-trip format. Callers persist this
    /// value and pass it back via <see cref="CloudStorageOptions.DeltaToken"/> for
    /// incremental runs. <see langword="null"/> when no messages were enumerated.
    /// Same-timestamp duplicates on the next run are caught by the hash-store skip.
    /// </summary>
    public string? GetDeltaToken() =>
        _maxReceived?.ToString("o", CultureInfo.InvariantCulture);

    protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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
            await foreach (var result in EnumerateFolderAsync(folders[f], filter, total, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return result;
                if (result.IsFailure || ++total >= _options.MaxResults)
                    yield break;
            }
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

                if (message.ReceivedDateTime is { } received &&
                    (_maxReceived is null || received > _maxReceived))
                {
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
                : Result<MessageCollectionResponse, RagError>.Failure(new RagError.HttpFailed(
                    HttpStatusCode.NoContent, $"Graph returned an empty response for folder '{folderId}'."));
        }
        catch (ODataError ex)
        {
            return Result<MessageCollectionResponse, RagError>.Failure(new RagError.HttpFailed(
                (HttpStatusCode)ex.ResponseStatusCode, ex.Error?.Message ?? ex.Message));
        }
        catch (ApiException ex)
        {
            return Result<MessageCollectionResponse, RagError>.Failure(new RagError.HttpFailed(
                (HttpStatusCode)ex.ResponseStatusCode, ex.Message));
        }
    }

    private FileHandle ToHandle(string folderId, Message message)
    {
        var messageId = message.Id!;
        var fileName  = string.IsNullOrWhiteSpace(message.Subject)
            ? $"message-{messageId}.eml"
            : $"{SanitizeFileName(message.Subject)}.eml";

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
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
            Metadata:         metadata);
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

    private static string SanitizeFileName(string subject)
    {
        // Mirrors the Gmail connector: replace invalid filename chars with underscore.
        var invalid = Path.GetInvalidFileNameChars();
        var safe    = new char[subject.Length];
        for (int i = 0; i < subject.Length; i++)
            safe[i] = Array.IndexOf(invalid, subject[i]) >= 0 ? '_' : subject[i];
        return new string(safe);
    }
}
