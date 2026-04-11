using BenchmarkDotNet.Attributes;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Bitbucket;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Bitbucket-specific benchmarks: full source tree traversal and delta (diffstat) traversal.
/// </summary>
[MemoryDiagnoser]
public class BitbucketBenchmarks
{
    private BitbucketDataProvider _fullProvider = default!;
    private BitbucketDataProvider _deltaProvider = default!;

    [GlobalSetup]
    public void Setup()
    {
        _fullProvider  = ConnectorIngestionBenchmarks.CreateBitbucketProvider(20, delta: false);
        _deltaProvider = ConnectorIngestionBenchmarks.CreateBitbucketProvider(20, delta: true);
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
    public async Task<int> DeltaTraversal()
    {
        int count = 0;
        await foreach (var file in _deltaProvider.GetFilesAsync(CancellationToken.None))
        {
            if (file.IsFailure) continue;
            await using var stream = await file.Value.OpenContentAsync(CancellationToken.None);
            count++;
        }
        return count;
    }
}
