using BenchmarkDotNet.Attributes;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.GitLab;

namespace Rag.NET.Benchmarks;

/// <summary>
/// GitLab-specific benchmarks: full tree traversal and delta (compare) traversal.
/// </summary>
[MemoryDiagnoser]
public class GitLabBenchmarks
{
    private GitLabDataProvider _fullProvider = default!;
    private GitLabDataProvider _deltaProvider = default!;

    [GlobalSetup]
    public void Setup()
    {
        _fullProvider  = ConnectorIngestionBenchmarks.CreateGitLabProvider(20, delta: false);
        _deltaProvider = ConnectorIngestionBenchmarks.CreateGitLabProvider(20, delta: true);
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
