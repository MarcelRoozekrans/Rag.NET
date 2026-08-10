using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Zendesk;

/// <summary>
/// Enumerates Zendesk support tickets as Markdown documents via the Zendesk REST API v2.
/// <para>
/// A full run uses <c>start_time=0</c> and paginates via cursor until
/// <c>end_of_stream</c> is <see langword="true"/>. A delta run uses
/// <see cref="ZendeskTicketsOptions.DeltaToken"/> as the <c>start_time</c> Unix epoch.
/// </para>
/// <para>
/// Each ticket is emitted as a <c>.md</c> file containing subject, status, priority,
/// description, and comments.
/// </para>
/// </summary>
public sealed class ZendeskTicketsDataProvider : FileContentProviderBase
{
    private readonly IZendeskApi _api;
    private readonly ZendeskTicketsOptions _options;

    internal ZendeskTicketsDataProvider(IZendeskApi api, ZendeskTicketsOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        _options = options;
    }

    protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long startTime = _options.DeltaToken is not null
            ? long.Parse(_options.DeltaToken, CultureInfo.InvariantCulture)
            : 0;

        string? cursor = null;
        bool endOfStream = false;

        while (!endOfStream)
        {
            var ticketsResult = await _api.GetIncrementalTicketsAsync(
                startTime, cursor, cancellationToken).ConfigureAwait(false);

            if (ticketsResult.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(
                    new RagError.HttpFailed(ticketsResult.Error.StatusCode, ticketsResult.Error.Message));
                yield break;
            }

            var result = ticketsResult.Value;

            for (int i = 0; i < result.Tickets.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var commentsResult = await _api.GetTicketCommentsAsync(
                    result.Tickets[i].Id, cancellationToken).ConfigureAwait(false);

                if (commentsResult.IsFailure)
                {
                    yield return Result<FileHandle, RagError>.Failure(
                        new RagError.HttpFailed(commentsResult.Error.StatusCode, commentsResult.Error.Message));
                    yield break;
                }

                yield return Result<FileHandle, RagError>.Success(
                    ToHandle(result.Tickets[i], commentsResult.Value.Comments));
            }

            endOfStream = result.EndOfStream;
            cursor = result.AfterCursor;

            // Store the end time as delta token for the next run.
            // Callers can read it from the last emitted file's metadata or from the provider.
            _lastEndTime = result.EndTime;
        }
    }

    private long _lastEndTime;

    /// <summary>
    /// Returns the delta token from the last completed traversal.
    /// Callers can persist this value and pass it back via
    /// <see cref="ZendeskTicketsOptions.DeltaToken"/> for incremental runs.
    /// </summary>
    public string? GetDeltaToken() =>
        _lastEndTime > 0 ? _lastEndTime.ToString(CultureInfo.InvariantCulture) : null;

    // Instance rather than static so the Zendesk subdomain — the container context callers
    // filter on — is reachable from _options.
    private FileHandle ToHandle(ZendeskTicket ticket, IReadOnlyList<ZendeskComment> comments)
    {
        var markdown = ToMarkdown(ticket, comments);
        return new FileHandle(
            Id: ticket.Id.ToString(CultureInfo.InvariantCulture),
            FileName: $"ticket-{ticket.Id}.md",
            ETag: ticket.UpdatedAt,
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))),
            Metadata: BuildMetadata(ticket),
            UpdatedAt: ConnectorTimestampParser.Parse(ticket.UpdatedAt));
    }

    /// <summary>
    /// The ticket's filterable fields. Status and priority are <i>also</i> rendered into the
    /// Markdown body by <see cref="ToMarkdown"/>: the body drives semantic recall, the tags
    /// drive filtering, and neither substitutes for the other.
    /// </summary>
    private Dictionary<string, MetadataValue> BuildMetadata(ZendeskTicket ticket)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["ticket_id"] = ticket.Id.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(ticket.Status))    metadata["status"]     = ticket.Status;
        if (!string.IsNullOrEmpty(ticket.Priority))  metadata["priority"]   = ticket.Priority;
        if (!string.IsNullOrEmpty(_options.Subdomain)) metadata["subdomain"] = _options.Subdomain;
        return metadata;
    }

    private static string ToMarkdown(ZendeskTicket ticket, IReadOnlyList<ZendeskComment> comments)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {ticket.Subject}");
        sb.AppendLine();
        sb.Append($"**Status:** {ticket.Status}");
        if (ticket.Priority is not null)
            sb.Append($"  **Priority:** {ticket.Priority}");
        sb.AppendLine();
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(ticket.Description))
        {
            sb.AppendLine(ticket.Description);
            sb.AppendLine();
        }
        if (comments.Count > 0)
        {
            sb.AppendLine("## Comments");
            for (int i = 0; i < comments.Count; i++)
                sb.AppendLine(CultureInfo.InvariantCulture, $"**{comments[i].AuthorId}** ({comments[i].CreatedAt}): {comments[i].Body}");
        }
        return sb.ToString().TrimEnd();
    }
}
