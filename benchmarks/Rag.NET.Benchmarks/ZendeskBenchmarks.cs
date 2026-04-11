using BenchmarkDotNet.Attributes;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Zendesk;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Zendesk-specific benchmarks: tickets with comments, articles with HTML bodies,
/// and HTML stripping for large article content.
/// </summary>
[MemoryDiagnoser]
public class ZendeskBenchmarks
{
    private ZendeskTicketsDataProvider _ticketsProvider = default!;
    private ZendeskArticlesDataProvider _articlesProvider = default!;
    private ZendeskArticlesDataProvider _largeHtmlProvider = default!;

    [GlobalSetup]
    public void Setup()
    {
        // 20 tickets with 2 comments each
        _ticketsProvider = ConnectorIngestionBenchmarks.CreateZendeskTicketsProvider(20, commentsPerTicket: 2);

        // 20 articles with small HTML bodies
        _articlesProvider = ConnectorIngestionBenchmarks.CreateZendeskArticlesProvider(20, largeHtml: false);

        // 5 articles with ~10KB HTML bodies to stress the regex-based HTML stripping
        _largeHtmlProvider = ConnectorIngestionBenchmarks.CreateZendeskArticlesProvider(5, largeHtml: true);
    }

    [Benchmark]
    public async Task<int> TicketsFullTraversal()
    {
        int count = 0;
        await foreach (var file in _ticketsProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> ArticlesFullTraversal()
    {
        int count = 0;
        await foreach (var file in _articlesProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> ArticlesHtmlStripping()
    {
        int count = 0;
        await foreach (var file in _largeHtmlProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }
}
