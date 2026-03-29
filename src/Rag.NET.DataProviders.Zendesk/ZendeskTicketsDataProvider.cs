using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.DataProviders;

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

    protected override async IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long startTime = _options.DeltaToken is not null
            ? long.Parse(_options.DeltaToken, CultureInfo.InvariantCulture)
            : 0;

        string? cursor = null;
        bool endOfStream = false;

        while (!endOfStream)
        {
            var result = await _api.GetIncrementalTicketsAsync(
                startTime, cursor, cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < result.Tickets.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return await ToHandleAsync(result.Tickets[i], cancellationToken)
                    .ConfigureAwait(false);
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

    private async Task<FileHandle> ToHandleAsync(ZendeskTicket ticket, CancellationToken cancellationToken)
    {
        var commentsPage = await _api.GetTicketCommentsAsync(
            ticket.Id, cancellationToken).ConfigureAwait(false);

        var markdown = ToMarkdown(ticket, commentsPage.Comments);
        return new FileHandle(
            Id: ticket.Id.ToString(CultureInfo.InvariantCulture),
            FileName: $"ticket-{ticket.Id}.md",
            ETag: ticket.UpdatedAt,
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
    }

    private static string ToMarkdown(ZendeskTicket ticket, List<ZendeskComment> comments)
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
