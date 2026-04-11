using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Confluence;
using Refit;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Confluence-specific benchmarks: full traversal, delta traversal, and large HTML bodies.
/// </summary>
[MemoryDiagnoser]
public class ConfluenceBenchmarks
{
    private ConfluenceDataProvider _fullProvider = default!;
    private ConfluenceDataProvider _deltaProvider = default!;
    private ConfluenceDataProvider _largeHtmlProvider = default!;

    [GlobalSetup]
    public void Setup()
    {
        // Full traversal: 20 items
        var fullJson = ConnectorIngestionBenchmarks.BuildConfluenceJson(20);
        _fullProvider = MakeProvider(fullJson, "/wiki/rest/api/content");

        // Delta traversal: 20 items via search endpoint
        var deltaJson = ConnectorIngestionBenchmarks.BuildConfluenceJson(20);
        _deltaProvider = MakeProvider(deltaJson, "/wiki/rest/api/content/search",
            new ConfluenceOptions
            {
                BaseUrl    = "https://bench.atlassian.net",
                Email      = "bench@test.com",
                DeltaToken = "2026-01-01T00:00:00Z"
            });

        // Large HTML bodies: 5 items with ~10KB HTML each
        _largeHtmlProvider = MakeLargeHtmlProvider(5, 10_000);
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
    public async Task<int> LargeHtmlBodies()
    {
        int count = 0;
        await foreach (var file in _largeHtmlProvider.GetFilesAsync(CancellationToken.None))
        {
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    private static ConfluenceDataProvider MakeProvider(
        string json, string urlKey, ConfluenceOptions? options = null)
    {
        var handler = new BenchFakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
            { [urlKey] = json });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://bench.atlassian.net") };
        var api = RestService.For<IConfluenceApi>(http);
        return new ConfluenceDataProvider(api, options ?? new ConfluenceOptions
        {
            BaseUrl = "https://bench.atlassian.net",
            Email   = "bench@test.com"
        });
    }

    private static ConfluenceDataProvider MakeLargeHtmlProvider(int count, int htmlSize)
    {
        var filler = new string('x', htmlSize / 20); // repeat in paragraphs
        var sb = new StringBuilder();
        sb.Append("""{ "results": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            var htmlSb = new StringBuilder();
            for (int p = 0; p < 20; p++)
                htmlSb.Append(CultureInfo.InvariantCulture, $"<p>Paragraph {p} of page {i}: {filler}</p>");
            var htmlBody = htmlSb.ToString().Replace("\"", "\\\"", StringComparison.Ordinal);
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "id": "{{i}}", "title": "LargePage {{i}}", "body": { "storage": { "value": "{{htmlBody}}" } }, "version": { "number": 1 } }
                """);
        }
        sb.Append("""], "_links": {} }""");

        return MakeProvider(sb.ToString(), "/wiki/rest/api/content");
    }
}
