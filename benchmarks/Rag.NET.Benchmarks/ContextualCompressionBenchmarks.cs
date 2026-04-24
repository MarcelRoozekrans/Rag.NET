using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Models;
using Rag.NET.QueryTechniques.ContextualCompression;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the sentence-splitting, cosine-scoring, and selection overhead of
/// <see cref="ExtractiveCompressor"/>. Uses a synchronous fake embedder that returns
/// deterministic vectors so results reflect library CPU cost, not I/O.
/// </summary>
[MemoryDiagnoser]
public class ContextualCompressionBenchmarks
{
    private ExtractiveCompressor _topN = null!;
    private ExtractiveCompressor _tokenBudget = null!;
    private List<SearchResult> _small = null!;
    private List<SearchResult> _large = null!;

    [GlobalSetup]
    public void Setup()
    {
        var embedder = new FakeEmbedder();
        _topN = new ExtractiveCompressor(
            embedder,
            new ContextualCompressionOptions { KeepTopSentences = 3 },
            NullLogger<ExtractiveCompressor>.Instance);
        _tokenBudget = new ExtractiveCompressor(
            embedder,
            new ContextualCompressionOptions { KeepTopSentences = null, MaxTokensPerChunk = 50 },
            NullLogger<ExtractiveCompressor>.Instance);

        _small = Enumerable.Range(0, 5)
            .Select(i => Result(
                $"Short sentence about topic {i}. Another short one. And one more.",
                $"d{i}"))
            .ToList();

        _large = Enumerable.Range(0, 5)
            .Select(i =>
            {
                var sb = new StringBuilder();
                for (var j = 0; j < 50; j++)
                    sb.Append("Long paragraph sentence number ").Append(j).Append(". ");
                return Result(sb.ToString(), $"d{i}");
            })
            .ToList();
    }

    /// <summary>5 short chunks, top-3 sentence selection.</summary>
    [Benchmark]
    public async Task TopN_SmallChunks()
        => _ = await _topN.CompressAsync(_small, "query", CancellationToken.None).ConfigureAwait(false);

    /// <summary>5 long (50-sentence) chunks, top-3 sentence selection.</summary>
    [Benchmark]
    public async Task TopN_LargeChunks()
        => _ = await _topN.CompressAsync(_large, "query", CancellationToken.None).ConfigureAwait(false);

    /// <summary>5 long (50-sentence) chunks, 50-token budget selection (tokenizer on hot path).</summary>
    [Benchmark]
    public async Task TokenBudget_LargeChunks()
        => _ = await _tokenBudget.CompressAsync(_large, "query", CancellationToken.None).ConfigureAwait(false);

    private static SearchResult Result(string text, string id) =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(id), ChunkIndex = 0 },
            Score = 0.5,
        };

    private sealed class FakeEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public EmbeddingGeneratorMetadata Metadata { get; } = new("fake");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
                result.Add(new Embedding<float>(new[] { 0.1f, 0.2f, 0.3f }));
            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
