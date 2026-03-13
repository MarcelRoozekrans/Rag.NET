using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Rag.NET.Models;
using Rag.NET.PostRetrieval;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the CPU-only path of RedundancyFilter (re-embedding is mocked to be synchronous/zero-latency).
/// Real-world cost is dominated by the embedding API call; these numbers isolate the cosine-similarity filter loop.
/// </summary>
[MemoryDiagnoser]
public class RedundancyFilterBenchmarks
{
    private IReadOnlyList<SearchResult> _results5 = null!;
    private IReadOnlyList<SearchResult> _results20 = null!;
    private IEmbeddingGenerator<string, Embedding<float>> _embedder = null!;

    private const int Dimensions = 384;

    [GlobalSetup]
    public void Setup()
    {
        _results5 = BuildResults(5, Dimensions);
        _results20 = BuildResults(20, Dimensions);
        _embedder = new SyncFakeEmbeddingGenerator(Dimensions);
    }

    [Benchmark]
    public async Task<int> Filter_TopK5()
    {
        var filtered = await RedundancyFilter.FilterAsync(_results5, _embedder, 0.95f).ConfigureAwait(false);
        return filtered.Count;
    }

    [Benchmark]
    public async Task<int> Filter_TopK20()
    {
        var filtered = await RedundancyFilter.FilterAsync(_results20, _embedder, 0.95f).ConfigureAwait(false);
        return filtered.Count;
    }

    private static IReadOnlyList<SearchResult> BuildResults(int count, int dimensions)
    {
        var rng = new Random(42);
        var results = new SearchResult[count];
        for (int i = 0; i < count; i++)
        {
            var vec = new float[dimensions];
            foreach (ref var v in vec.AsSpan())
                v = (float)(rng.NextDouble() * 2 - 1);

            results[i] = new SearchResult
            {
                Chunk = new TextChunk { Text = $"chunk {i}", DocumentId = "doc", ChunkIndex = i },
                Score = 1.0 - (i * 0.01),
            };
        }

        return results;
    }

    private sealed class SyncFakeEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly Random _rng = new(99);

        public EmbeddingGeneratorMetadata Metadata { get; } = new("fake");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
            {
                var vec = new float[dimensions];
                foreach (ref var v in vec.AsSpan())
                    v = (float)(_rng.NextDouble() * 2 - 1);
                result.Add(new Embedding<float>(vec));
            }

            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
