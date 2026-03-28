using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Jira;

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

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => GetHandlesAsync(BuildJql(), cancellationToken);

    private string BuildJql()
    {
        var parts = new List<string>();
        if (_options.ProjectKey is not null)
            parts.Add($"project = \"{_options.ProjectKey}\"");
        if (_options.DeltaToken is not null)
            parts.Add($"updated > \"{_options.DeltaToken}\"");
        parts.Add(_options.Jql);
        return string.Join(" AND ", parts);
    }

    private async IAsyncEnumerable<FileHandle> GetHandlesAsync(
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
                yield return ToHandle(result.Issues[i]);
            }

            startAt += result.Issues.Count;
            if (startAt >= result.Total) break;
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
