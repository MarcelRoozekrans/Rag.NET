using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Jira;
using Refit;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Jira-specific benchmarks: full traversal, delta traversal, and issues with many comments.
/// </summary>
[MemoryDiagnoser]
public class JiraBenchmarks
{
    private JiraDataProvider _fullProvider = default!;
    private JiraDataProvider _deltaProvider = default!;
    private JiraDataProvider _manyCommentsProvider = default!;

    [GlobalSetup]
    public void Setup()
    {
        // Full traversal: 20 issues
        var fullJson = ConnectorIngestionBenchmarks.BuildJiraJson(20);
        _fullProvider = MakeProvider(fullJson);

        // Delta traversal: 20 issues (same JSON, different options trigger JQL filter)
        _deltaProvider = MakeProvider(fullJson, new JiraOptions
        {
            BaseUrl    = "https://bench.atlassian.net",
            Email      = "bench@test.com",
            DeltaToken = "2026-01-01T00:00:00Z"
        });

        // Issues with many comments: 5 issues, 10 comments each
        _manyCommentsProvider = MakeProvider(BuildJiraWithComments(5, 10));
    }

    [Benchmark]
    public async Task<int> FullTraversal()
    {
        int count = 0;
        await foreach (var file in _fullProvider.GetFilesAsync(CancellationToken.None))
        {
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> DeltaTraversal()
    {
        int count = 0;
        await foreach (var file in _deltaProvider.GetFilesAsync(CancellationToken.None))
        {
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> IssueWithManyComments()
    {
        int count = 0;
        await foreach (var file in _manyCommentsProvider.GetFilesAsync(CancellationToken.None))
        {
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    private static JiraDataProvider MakeProvider(string json, JiraOptions? options = null)
    {
        var handler = new BenchFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
            { ["/rest/api/3/search"] = json });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://bench.atlassian.net") };
        var api = RestService.For<IJiraApi>(http);
        return new JiraDataProvider(api, options ?? new JiraOptions
        {
            BaseUrl = "https://bench.atlassian.net",
            Email   = "bench@test.com"
        });
    }

    private static string BuildJiraWithComments(int issueCount, int commentCount)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "issues": [""");
        for (int i = 0; i < issueCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""{ "id": "{{10000 + i}}", "key": "BENCH-{{i + 1}}", "fields": { "summary": "Issue {{i}}", "description": "Description", "status": { "name": "Open" }, "priority": { "name": "High" }, "assignee": { "displayName": "Dev" }, "comment": { "comments": [""");
            for (int c = 0; c < commentCount; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append(CultureInfo.InvariantCulture, $$"""{ "author": { "displayName": "Commenter {{c}}" }, "created": "2026-03-01T{{10 + c}}:00:00Z", "body": "Comment {{c}} on issue {{i}} with some detail text." }""");
            }
            sb.Append("""] }, "updated": "2026-03-01T10:00:00Z" } }""");
        }
        sb.Append(CultureInfo.InvariantCulture, $$"""], "total": {{issueCount}} }""");
        return sb.ToString();
    }
}
