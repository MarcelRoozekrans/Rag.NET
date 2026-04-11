using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using Refit;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Jira;

/// <summary>
/// Enumerates Jira issues as Markdown documents via the Jira REST API.
/// <para>
/// A full run executes the configured JQL query, optionally scoped to
/// <see cref="JiraOptions.ProjectKey"/>. A delta run prepends a
/// <c>updated &gt;</c> clause using <see cref="JiraOptions.DeltaToken"/>.
/// When the Atlassian API returns HTTP 400 (stale or invalid token) the provider
/// falls back to a full traversal automatically.
/// </para>
/// <para>
/// Each issue is emitted as a <c>.md</c> file containing status, priority,
/// assignee, description, and comments.
/// </para>
/// </summary>
public sealed partial class JiraDataProvider : FileContentProviderBase
{
    [GeneratedRegex(@"^[A-Za-z0-9\-_]+$", RegexOptions.NonBacktracking)]
    private static partial Regex ProjectKeyRegex();

    [GeneratedRegex(@"^[A-Za-z0-9:\-\.TZ\+]+$", RegexOptions.NonBacktracking)]
    private static partial Regex DeltaTokenRegex();

    private readonly IJiraApi _api;
    private readonly JiraOptions _options;

    internal JiraDataProvider(IJiraApi api, JiraOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (options.DeltaToken is not null && !DeltaTokenRegex().IsMatch(options.DeltaToken))
            throw new ArgumentException(
                $"DeltaToken contains invalid characters: '{options.DeltaToken}'.", nameof(options));
        if (options.ProjectKey is not null && !ProjectKeyRegex().IsMatch(options.ProjectKey))
            throw new ArgumentException(
                $"ProjectKey contains invalid characters: '{options.ProjectKey}'.", nameof(options));
        _api     = api;
        _options = options;
    }

    protected override IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => _options.DeltaToken is not null
            ? GetDeltaHandlesAsync(cancellationToken)
            : GetHandlesAsync(BuildFullJql(), cancellationToken);

    private string BuildFullJql()
    {
        var parts = new List<string>();
        if (_options.ProjectKey is not null)
            parts.Add($"project = \"{_options.ProjectKey}\"");
        parts.Add(_options.Jql);
        return string.Join(" AND ", parts);
    }

    private string BuildDeltaJql()
    {
        var parts = new List<string>();
        if (_options.ProjectKey is not null)
            parts.Add($"project = \"{_options.ProjectKey}\"");
        parts.Add($"updated > \"{_options.DeltaToken}\"");
        parts.Add(_options.Jql);
        return string.Join(" AND ", parts);
    }

    /// <summary>
    /// Delta traversal using a JQL <c>updated &gt;</c> filter.
    /// Falls back to a full traversal when the Atlassian API returns HTTP 400,
    /// which indicates a stale or otherwise invalid <see cref="JiraOptions.DeltaToken"/>.
    /// </summary>
    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetDeltaHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var deltaJql = BuildDeltaJql();
        const int maxResults = 50;

        // Attempt the first page before entering the yield-loop so we can catch API errors
        // before any items have been emitted. C# does not allow yield inside catch blocks,
        // so we use a flag to switch to full traversal after the try/catch.
        JiraSearchResult? firstPage = null;
        bool staleDeltaToken = false;
        try
        {
            firstPage = await _api.SearchAsync(deltaJql, maxResults, startAt: 0,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex)
            when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Stale or invalid token — signal a fall back to full traversal.
            staleDeltaToken = true;
        }

        if (staleDeltaToken)
        {
            await foreach (var h in GetHandlesAsync(BuildFullJql(), cancellationToken)
                .ConfigureAwait(false))
                yield return h;
            yield break;
        }

        for (int i = 0; i < firstPage!.Issues.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Result<FileHandle, RagError>.Success(ToHandle(firstPage.Issues[i]));
        }

        int startAt = firstPage.Issues.Count;
        while (startAt < firstPage.Total)
        {
            var page = await _api.SearchAsync(deltaJql, maxResults, startAt,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < page.Issues.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Result<FileHandle, RagError>.Success(ToHandle(page.Issues[i]));
            }

            startAt += page.Issues.Count;
            if (page.Issues.Count == 0) break; // guard against empty page
        }
    }

    private async IAsyncEnumerable<Result<FileHandle, RagError>> GetHandlesAsync(
        string jql,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int startAt = 0;
        const int maxResults = 50;

        while (true)
        {
            var result = await _api.SearchAsync(jql, maxResults, startAt,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < result.Issues.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Result<FileHandle, RagError>.Success(ToHandle(result.Issues[i]));
            }

            startAt += result.Issues.Count;
            if (result.Issues.Count == 0 || startAt >= result.Total) break;
        }
    }

    private static FileHandle ToHandle(JiraIssue issue)
    {
        var markdown = ToMarkdown(issue);
        return new FileHandle(
            Id:               issue.Key,
            FileName:         $"{issue.Key}.md",
            ETag:             issue.Fields.Updated,
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
    }

    private static string ToMarkdown(JiraIssue issue)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {issue.Fields.Summary}");
        sb.AppendLine();
        sb.Append($"**Status:** {issue.Fields.Status.Name}");
        if (issue.Fields.Priority is not null)
            sb.Append($"  **Priority:** {issue.Fields.Priority.Name}");
        if (issue.Fields.Assignee is not null)
            sb.Append($"  **Assignee:** {issue.Fields.Assignee.DisplayName}");
        sb.AppendLine();
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(issue.Fields.Description))
        {
            sb.AppendLine(issue.Fields.Description);
            sb.AppendLine();
        }
        var comments = issue.Fields.Comment?.Comments ?? [];
        if (comments.Count > 0)
        {
            sb.AppendLine("## Comments");
            for (int i = 0; i < comments.Count; i++)
                sb.AppendLine($"**{comments[i].Author.DisplayName}** ({comments[i].Created}): {comments[i].Body}");
        }
        return sb.ToString().TrimEnd();
    }
}
