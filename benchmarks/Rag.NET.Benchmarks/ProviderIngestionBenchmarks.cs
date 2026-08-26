using BenchmarkDotNet.Attributes;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using ZeroAlloc.Results;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Measures <see cref="RagPipelineExtensions.IngestFromProviderAsync"/> throughput across
/// three deduplication scenarios: no store (baseline), warm ETag cache (all skipped),
/// and cold store (all new files hashed and ingested).
/// </summary>
[MemoryDiagnoser]
public class ProviderIngestionBenchmarks
{
    private string _tempDir = null!;
    private string _warmDbPath = null!;
    private string _coldDbPath = null!;
    private IRagPipeline _pipeline = null!;
    private IRagPipeline _pipeline5ms = null!;

    [Params(20)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _pipeline = new NoOpRagPipeline();
        _pipeline5ms = new DelayedNoOpRagPipeline(TimeSpan.FromMilliseconds(5));

        // Create a temp directory with FileCount small text files
        _tempDir = Path.Combine(Path.GetTempPath(), $"ragnet-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        for (var i = 0; i < FileCount; i++)
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"doc{i:D4}.txt"),
                $"Content of document {i}. The quick brown fox jumps over the lazy dog.").ConfigureAwait(false);

        // Warm store: pre-populate with current ETags so all entries will be skipped
        _warmDbPath = Path.Combine(Path.GetTempPath(), $"ragnet-bench-warm-{Guid.NewGuid():N}.db");
        var warmStore = new SqliteContentHashStore(_warmDbPath);
        var seedProvider = new LocalFilesDataProvider(_tempDir);
        await foreach (var entry in seedProvider.GetFilesAsync().ConfigureAwait(false))
        {
            if (entry.IsFailure) continue;
            await warmStore.SetAsync(new ProviderId("bench"), entry.Value.Id, entry.Value.ETag, "placeholder-hash").ConfigureAwait(false);
        }

        _coldDbPath = Path.Combine(Path.GetTempPath(), $"ragnet-bench-cold-{Guid.NewGuid():N}.db");
    }

    [IterationSetup(Target = nameof(IngestFromProviderAsync_ColdStore_AllNew))]
    public void ColdSetup()
    {
        // Delete and recreate the cold DB so every iteration starts with an empty store
        if (File.Exists(_coldDbPath)) File.Delete(_coldDbPath);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        if (File.Exists(_warmDbPath)) File.Delete(_warmDbPath);
        if (File.Exists(_coldDbPath)) File.Delete(_coldDbPath);
    }

    /// <summary>Baseline: no hash store, every file is ingested unconditionally.</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> IngestFromProviderAsync_NoStore()
    {
        var provider = new LocalFilesDataProvider(_tempDir);
        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("bench")).ConfigureAwait(false);
        return result.IngestedCount;
    }

    /// <summary>Warm cache: all ETags match → every file skipped without reading content.</summary>
    [Benchmark]
    public async Task<int> IngestFromProviderAsync_WarmStore_AllSkipped()
    {
        var provider = new LocalFilesDataProvider(_tempDir);
        var store = new SqliteContentHashStore(_warmDbPath);
        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("bench"), hashStore: store).ConfigureAwait(false);
        return result.SkippedCount;
    }

    /// <summary>Cold store: every file is read, SHA-256 hashed, and ingested.</summary>
    [Benchmark]
    public async Task<int> IngestFromProviderAsync_ColdStore_AllNew()
    {
        var provider = new LocalFilesDataProvider(_tempDir);
        var store = new SqliteContentHashStore(_coldDbPath);
        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("bench"), hashStore: store).ConfigureAwait(false);
        return result.IngestedCount;
    }

    /// <summary>Sequential ingestion with a simulated 5 ms per-document processing delay.</summary>
    [Benchmark]
    public async Task<int> IngestFromProviderAsync_Sequential_WithDelay()
    {
        var provider = new LocalFilesDataProvider(_tempDir);
        var result = await _pipeline5ms.IngestFromProviderAsync(provider, new ProviderId("bench"),
            options: new IngestionOptions { MaxDegreeOfParallelism = 1 }).ConfigureAwait(false);
        return result.IngestedCount;
    }

    /// <summary>Parallel ingestion (4 workers) with a simulated 5 ms per-document processing delay.</summary>
    [Benchmark]
    public async Task<int> IngestFromProviderAsync_Parallel4_WithDelay()
    {
        var provider = new LocalFilesDataProvider(_tempDir);
        var result = await _pipeline5ms.IngestFromProviderAsync(provider, new ProviderId("bench"),
            options: new IngestionOptions { MaxDegreeOfParallelism = 4 }).ConfigureAwait(false);
        return result.IngestedCount;
    }

    private sealed class NoOpRagPipeline : IRagPipeline
    {
        public Task<Result<IngestionResult, RagError>> IngestAsync(
            Stream document,
            DocumentMetadata metadata,
            IngestionOptions? options = null,
            IProgress<IngestionProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IngestionResult, RagError>.Success(
                new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = 1 }));

        public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
            string query,
            RetrievalOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Success(
                (IReadOnlyList<SearchResult>)[]));

        public Task<RagResponse> AskAsync(
            string query,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
            string query,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class DelayedNoOpRagPipeline(TimeSpan delay) : IRagPipeline
    {
        public async Task<Result<IngestionResult, RagError>> IngestAsync(
            Stream document,
            DocumentMetadata metadata,
            IngestionOptions? options = null,
            IProgress<IngestionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return Result<IngestionResult, RagError>.Success(
                new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = 1 });
        }

        public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
            string query,
            RetrievalOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Success(
                (IReadOnlyList<SearchResult>)[]));

        public Task<RagResponse> AskAsync(
            string query,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
            string query,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
