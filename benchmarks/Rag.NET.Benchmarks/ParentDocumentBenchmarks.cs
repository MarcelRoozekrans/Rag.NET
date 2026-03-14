using BenchmarkDotNet.Attributes;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Storage;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the CPU-only overhead of the <see cref="ParentDocumentRetriever"/> decorator.
/// The inner retriever is mocked (returns pre-built child results) and the parent store is
/// in-memory, so there is zero I/O — these numbers measure only dictionary lookup and
/// deduplication overhead.
/// </summary>
[MemoryDiagnoser]
public class ParentDocumentBenchmarks
{
    private IRetriever _inner = null!;
    private InMemoryParentChunkStore _parentStore = null!;
    private ParentDocumentRetriever _retriever = null!;
    private IRetriever _baseline = null!;

    [GlobalSetup]
    public void Setup()
    {
        _parentStore = new InMemoryParentChunkStore();
        for (int i = 0; i < 5; i++)
            _parentStore.Add("doc1", i, $"Parent chunk {i}: this is the larger parent context text that replaces the small child chunk.");

        // Pre-build the child results list; the fake retriever returns it every call.
        var childResults = Enumerable.Range(0, 5).Select(i => new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = $"child chunk {i}",
                DocumentId = "doc1",
                ChunkIndex = i,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["_parentKey"] = $"doc1:{i}"
                }
            },
            Score = 0.9 - i * 0.1
        }).ToList();

        _inner = new FakeRetriever(childResults);
        _retriever = new ParentDocumentRetriever(_inner, _parentStore);

        // Baseline: a second fake that returns results directly (no parent lookup).
        _baseline = new FakeRetriever(childResults);
    }

    [Benchmark(Baseline = true)]
    public Task<IReadOnlyList<SearchResult>> NoParentDocument_Baseline()
        => _baseline.RetrieveAsync("test query", new RetrievalOptions { UseParentDocument = false });

    [Benchmark]
    public Task<IReadOnlyList<SearchResult>> WithParentDocument()
        => _retriever.RetrieveAsync("test query");

    /// <summary>
    /// Minimal <see cref="IRetriever"/> that always returns the same pre-built list.
    /// Simulates an inner retriever with zero I/O latency.
    /// </summary>
    private sealed class FakeRetriever(IReadOnlyList<SearchResult> results) : IRetriever
    {
        public Task<IReadOnlyList<SearchResult>> RetrieveAsync(
            string query,
            RetrievalOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(results);
    }
}
