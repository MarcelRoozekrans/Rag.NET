using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Linear;

/// <summary>
/// Enumerates Linear issues as Markdown documents via the Linear GraphQL API — the repo's
/// first GraphQL connector, built on the ZeroAlloc.Rest POST-with-<c>[Body]</c> pattern.
/// <para>
/// Each issue becomes one <c>.md</c> <see cref="FileHandle"/> (<c>Id</c> = issue identifier,
/// <c>ETag</c> = <c>updatedAt</c>) containing the title heading, a state/project/assignee
/// line, the description, and a <c>## Comments</c> section. Team/state filters from
/// <see cref="LinearOptions"/> and the <see cref="CloudStorageOptions.DeltaToken"/>
/// watermark are sent as typed GraphQL variables; pages are followed via Relay cursors
/// until <c>hasNextPage</c> is <see langword="false"/>.
/// </para>
/// <para>
/// A delta run applies <see cref="CloudStorageOptions.DeltaToken"/> as an
/// <c>updatedAt gt</c> filter; <see cref="GetDeltaToken"/> returns the max
/// <c>updatedAt</c> seen — but only after a <b>complete</b> traversal (see the member doc).
/// GraphQL-level <c>errors</c> become <see cref="RagError.HttpFailed"/> failures naming
/// the error messages.
/// </para>
/// <para>
/// Watermark state (<see cref="GetDeltaToken"/>) is per-instance and not safe under
/// concurrent <c>IngestFromProviderAsync</c> calls against the same registration — the
/// documented run-then-persist flow is assumed.
/// </para>
/// </summary>
public sealed class LinearDataProvider : FileContentProviderBase
{
    /// <summary>
    /// The issues query. <c>orderBy: updatedAt</c> keeps page contents stable while
    /// paginating a delta window; Linear does not document the sort <i>direction</i>,
    /// so the watermark logic never assumes ascending order (see <see cref="GetDeltaToken"/>).
    /// </summary>
    internal const string IssuesQuery = """
        query Issues($first: Int!, $after: String, $filter: IssueFilter) {
          issues(first: $first, after: $after, filter: $filter, orderBy: updatedAt) {
            nodes {
              identifier
              title
              description
              updatedAt
              url
              state { name type }
              project { name }
              assignee { name }
              team { key }
              comments(first: 100) { nodes { body createdAt user { name } } pageInfo { hasNextPage } }
            }
            pageInfo { hasNextPage endCursor }
          }
        }
        """;

    /// <summary>
    /// Comments fetched per issue (the <c>first: 100</c> literal in <see cref="IssuesQuery"/>).
    /// When an issue has more, the entry is flagged <c>comments_truncated</c> and a warning
    /// is logged — comment pagination is out of scope for v1.
    /// </summary>
    internal const int CommentsPageSize = 100;

    private readonly ILinearApi    _api;
    private readonly LinearOptions _options;
    private readonly ILogger<LinearDataProvider>? _logger;

    private DateTimeOffset? _maxUpdated;
    private bool            _completedTraversal;

    internal LinearDataProvider(
        ILinearApi api,
        LinearOptions options,
        ILogger<LinearDataProvider>? logger = null)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api     = api;
        _options = options;
        _logger  = logger;
    }

    /// <summary>
    /// Returns the watermark from the last enumeration in ISO-8601 round-trip format, or
    /// <see langword="null"/> when it must not be advanced. Callers persist this value and
    /// pass it back via <see cref="CloudStorageOptions.DeltaToken"/> for incremental runs.
    /// <para>
    /// Linear does not document the sort direction of <c>orderBy: updatedAt</c>, so unseen
    /// issues cannot be assumed newer than the last-seen one. The max <c>updatedAt</c> is
    /// therefore only a safe watermark when the traversal <b>completed</b> (reached
    /// <c>hasNextPage: false</c> without a failure). A run that failed mid-pagination, was
    /// cancelled, or saw no issues returns <see langword="null"/> — meaning <b>keep the
    /// previous token</b>; advancing it would permanently skip the unseen issues.
    /// </para>
    /// <para>
    /// Persist the token only after an error-free run: the watermark is computed during
    /// enumeration, before per-entry ingestion outcomes are known. Issues updated at
    /// exactly the watermark instant are excluded by the <c>gt</c> comparison on the next
    /// run — they were already seen in the run that produced the token.
    /// </para>
    /// </summary>
    public string? GetDeltaToken() => _completedTraversal
        ? _maxUpdated?.ToString("o", CultureInfo.InvariantCulture)
        : null;

    protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _maxUpdated         = null;
        _completedTraversal = false;

        var (filter, filterError) = BuildFilter();
        if (filterError is not null)
        {
            yield return Result<FileHandle, RagError>.Failure(filterError);
            yield break;
        }

        string? after = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageResult = await FetchPageAsync(after, filter, cancellationToken)
                .ConfigureAwait(false);
            if (pageResult.IsFailure)
            {
                yield return Result<FileHandle, RagError>.Failure(pageResult.Error);
                yield break;
            }

            var page  = pageResult.Value;
            var nodes = page.Nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                var issue = nodes[i];
                if (_maxUpdated is null || issue.UpdatedAt > _maxUpdated)
                    _maxUpdated = issue.UpdatedAt;

                yield return Result<FileHandle, RagError>.Success(ToHandle(issue));
            }

            if (!page.PageInfo.HasNextPage)
                break;

            if (string.IsNullOrEmpty(page.PageInfo.EndCursor))
            {
                // Malformed page: hasNextPage without a cursor to follow. The traversal is
                // incomplete, so the watermark stays withheld (GetDeltaToken returns null).
                if (_logger is not null)
                    LinearLog.PaginationStoppedMalformedPage(_logger, page.PageInfo.EndCursor);
                yield break;
            }

            after = page.PageInfo.EndCursor;
        }

        _completedTraversal = true;
    }

    private (LinearIssueFilter? Filter, RagError? Error) BuildFilter()
    {
        LinearDateComparator? updatedAt = null;
        if (_options.DeltaToken is not null)
        {
            if (!DateTimeOffset.TryParse(
                    _options.DeltaToken, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var since))
            {
                return (null, new RagError.ValidationFailed(
                    [new ValidationFailure(
                        nameof(CloudStorageOptions.DeltaToken),
                        $"Invalid DeltaToken '{_options.DeltaToken}': expected an ISO-8601 updatedAt watermark.")]));
            }

            updatedAt = new LinearDateComparator(since);
        }

        var team = _options.TeamKeys is { Count: > 0 }
            ? new LinearTeamFilter(new LinearStringInComparator(_options.TeamKeys))
            : null;
        var state = _options.States is { Count: > 0 }
            ? new LinearStateFilter(new LinearStringInComparator(_options.States))
            : null;

        var filter = team is null && state is null && updatedAt is null
            ? null
            : new LinearIssueFilter(team, state, updatedAt);
        return (filter, null);
    }

    private async Task<Result<LinearIssueConnection, RagError>> FetchPageAsync(
        string? after, LinearIssueFilter? filter, CancellationToken ct)
    {
        var request = new LinearGraphQlRequest(
            IssuesQuery, new LinearIssueVariables(_options.PageSize, after, filter));

        var result = await _api.QueryAsync(request, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Result<LinearIssueConnection, RagError>.Failure(new RagError.HttpFailed(
                result.Error.StatusCode, result.Error.Message ?? string.Empty));
        }

        var response = result.Value;
        if (response.Errors is { Count: > 0 } errors)
        {
            var messages = new string[errors.Count];
            for (int i = 0; i < errors.Count; i++)
                messages[i] = errors[i].Message;
            // GraphQL errors arrive with HTTP 200; surface them as a failed result naming
            // the messages so they land in ProviderIngestionResult.Errors.
            return Result<LinearIssueConnection, RagError>.Failure(new RagError.HttpFailed(
                HttpStatusCode.OK, $"Linear GraphQL errors: {string.Join("; ", messages)}"));
        }

        if (response.Data is null)
        {
            return Result<LinearIssueConnection, RagError>.Failure(new RagError.HttpFailed(
                HttpStatusCode.OK, "Linear GraphQL response contained neither data nor errors."));
        }

        return Result<LinearIssueConnection, RagError>.Success(response.Data.Issues);
    }

    private FileHandle ToHandle(LinearIssue issue)
    {
        var markdown = ToMarkdown(issue);

        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["url"] = issue.Url,
        };
        if (issue.Team is not null)    metadata["team"]    = issue.Team.Key;
        if (issue.State is not null)
        {
            metadata["state"]      = issue.State.Name;
            // The state *type* category is more useful than the display name for
            // downstream filtering (display names are workspace-specific).
            metadata["state_type"] = issue.State.Type;
        }
        if (issue.Project is not null) metadata["project"] = issue.Project.Name;

        if (issue.Comments is { PageInfo.HasNextPage: true })
        {
            metadata["comments_truncated"] = "true";
            if (_logger is not null)
                LinearLog.CommentsTruncated(_logger, issue.Identifier, CommentsPageSize);
        }

        return new FileHandle(
            Id:               issue.Identifier,
            FileName:         $"{FileNameSanitizer.Sanitize(
                $"{issue.Identifier} {issue.Title}", $"issue-{issue.Identifier}")}.md",
            ETag:             issue.UpdatedAt.ToString("o", CultureInfo.InvariantCulture),
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))),
            Metadata:         metadata,
            UpdatedAt:        issue.UpdatedAt.UtcDateTime);
    }

    /// <summary>
    /// Renders the issue as Markdown. User-supplied Markdown in the description and comment
    /// bodies is intentionally <b>not</b> escaped — it is acceptable (and useful) for RAG
    /// ingestion; titles are single-line by Linear's product constraints.
    /// </summary>
    private static string ToMarkdown(LinearIssue issue)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {issue.Identifier}: {issue.Title}");
        sb.AppendLine();

        AppendMetadataLine(sb, issue);

        if (!string.IsNullOrWhiteSpace(issue.Description))
        {
            sb.AppendLine(issue.Description);
            sb.AppendLine();
        }

        AppendComments(sb, issue.Comments);
        return sb.ToString().TrimEnd();
    }

    private static void AppendMetadataLine(StringBuilder sb, LinearIssue issue)
    {
        bool any = false;
        if (issue.State is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"**State:** {issue.State.Name}");
            any = true;
        }
        if (issue.Project is not null)
        {
            if (any) sb.Append("  ");
            sb.Append(CultureInfo.InvariantCulture, $"**Project:** {issue.Project.Name}");
            any = true;
        }
        if (issue.Assignee is not null)
        {
            if (any) sb.Append("  ");
            sb.Append(CultureInfo.InvariantCulture, $"**Assignee:** {issue.Assignee.Name}");
            any = true;
        }
        if (any)
        {
            sb.AppendLine();
            sb.AppendLine();
        }
    }

    private static void AppendComments(StringBuilder sb, LinearCommentConnection? comments)
    {
        if (comments is not { Nodes.Count: > 0 })
            return;

        sb.AppendLine("## Comments");
        for (int i = 0; i < comments.Nodes.Count; i++)
        {
            var comment   = comments.Nodes[i];
            var author    = comment.User?.Name ?? "unknown";
            var createdAt = comment.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            sb.AppendLine(CultureInfo.InvariantCulture, $"**{author}** ({createdAt}): {comment.Body}");
        }
    }
}
