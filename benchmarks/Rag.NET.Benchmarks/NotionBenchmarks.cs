using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Notion;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Notion-specific benchmarks: full traversal (search + blocks) and many blocks per page.
/// </summary>
[MemoryDiagnoser]
public class NotionBenchmarks
{
    private NotionDataProvider _fullProvider = default!;
    private NotionDataProvider _manyBlocksProvider = default!;

    [GlobalSetup]
    public void Setup()
    {
        // Full traversal: 20 pages with empty block lists
        _fullProvider = ConnectorIngestionBenchmarks.CreateNotionProvider();

        // Many blocks per page: 5 pages, 50 blocks each
        _manyBlocksProvider = MakeManyBlocksProvider(5, 50);
    }

    [Benchmark]
    public async Task<int> FullTraversal()
    {
        int count = 0;
        await foreach (var file in _fullProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> ManyBlocksPerPage()
    {
        int count = 0;
        await foreach (var file in _manyBlocksProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    private static NotionDataProvider MakeManyBlocksProvider(int pageCount, int blocksPerPage)
    {
        var searchJson = BuildNotionSearch(pageCount);
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/v1/search"] = searchJson
        };

        for (int p = 0; p < pageCount; p++)
        {
            var blocksJson = BuildBlocks(blocksPerPage);
            responses[$"page-{p:D3}"] = blocksJson;
        }

        var handler = new BenchFakeHandler(responses);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.notion.com") };
        var api = new NotionApiClient(http, new SystemTextJsonSerializer());
        return new NotionDataProvider(api, new NotionOptions());
    }

    private static string BuildNotionSearch(int count)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "results": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "id": "page-{{i:D3}}", "last_edited_time": "2026-03-01T10:00:00.000Z", "properties": { "title": { "title": [{ "plain_text": "Page {{i}}" }] } } }
                """);
        }
        sb.Append("""], "has_more": false }""");
        return sb.ToString();
    }

    private static string BuildBlocks(int count)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "results": [""");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $$"""
                { "type": "paragraph", "paragraph": { "rich_text": [{ "plain_text": "Block content {{i}} with some filler text for benchmarking." }] } }
                """);
        }
        sb.Append("""], "has_more": false }""");
        return sb.ToString();
    }
}
