using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Graph;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.MicrosoftTeams;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Microsoft Teams-specific benchmarks: single-day batch, multi-day batch, and HTML body stripping.
/// </summary>
[MemoryDiagnoser]
public class TeamsBenchmarks
{
    private MicrosoftTeamsDataProvider _singleDayProvider = default!;
    private MicrosoftTeamsDataProvider _multiDayProvider = default!;
    private MicrosoftTeamsDataProvider _htmlStrippingProvider = default!;

    [GlobalSetup]
    public void Setup()
    {
        // Single day: 20 messages on the same day
        _singleDayProvider = ConnectorIngestionBenchmarks.CreateTeamsProvider();

        // Multi-day: 20 messages across 5 days
        _multiDayProvider = MakeMultiDayProvider(20, 5);

        // HTML stripping: 20 messages with HTML bodies
        _htmlStrippingProvider = MakeHtmlStrippingProvider(20);
    }

    [Benchmark]
    public async Task<int> SingleDayBatch()
    {
        int count = 0;
        await foreach (var file in _singleDayProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> MultiDayBatch()
    {
        int count = 0;
        await foreach (var file in _multiDayProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> HtmlStripping()
    {
        int count = 0;
        await foreach (var file in _htmlStrippingProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    private static MicrosoftTeamsDataProvider MakeTeamsProvider(string messagesJson)
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/me/joinedTeams"]                  = """{ "value": [{ "id": "team-1", "displayName": "Bench" }] }""",
            ["/team-1/channels"]                 = """{ "value": [{ "id": "chan-1", "displayName": "general" }] }""",
            ["/team-1/channels/chan-1/messages"]  = messagesJson,
        };
        var handler = new BenchFakeHandler(responses);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") };
        return new MicrosoftTeamsDataProvider(new GraphServiceClient(httpClient), new MicrosoftTeamsOptions());
    }

    private static MicrosoftTeamsDataProvider MakeMultiDayProvider(int count, int days)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "value": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            int day = (i % days) + 1;
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "id": "msg-{{i}}", "createdDateTime": "2026-03-{{day:D2}}T10:{{i % 60:D2}}:00Z", "lastModifiedDateTime": "2026-03-{{day:D2}}T10:{{i % 60:D2}}:00Z", "from": { "user": { "displayName": "User {{i}}" } }, "body": { "content": "Multi-day message {{i}}", "contentType": "text" } }
                """);
        }
        sb.Append("] }");
        return MakeTeamsProvider(sb.ToString());
    }

    private static MicrosoftTeamsDataProvider MakeHtmlStrippingProvider(int count)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "value": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "id": "msg-{{i}}", "createdDateTime": "2026-03-01T10:{{i % 60:D2}}:00Z", "lastModifiedDateTime": "2026-03-01T10:{{i % 60:D2}}:00Z", "from": { "user": { "displayName": "User {{i}}" } }, "body": { "content": "<div><p><b>Bold</b> and <i>italic</i> message {{i}} with <a href='#'>links</a> and <code>code</code></p></div>", "contentType": "html" } }
                """);
        }
        sb.Append("] }");
        return MakeTeamsProvider(sb.ToString());
    }
}
