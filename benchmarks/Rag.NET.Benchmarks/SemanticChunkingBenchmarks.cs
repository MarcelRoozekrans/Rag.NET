using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks the CPU and allocation overhead of SemanticChunkingStrategy.
/// The embedding generator is mocked (returns fixed zero vectors) to isolate the
/// chunking coordination cost. Real-world cost is dominated by embedding model calls.
/// </summary>
[MemoryDiagnoser]
public class SemanticChunkingBenchmarks
{
    private DocumentSection _smallSection = null!;
    private DocumentSection _largeSection = null!;
    private SemanticChunkingStrategy _semantic = null!;
    private readonly ChunkingOptions _options = new() { MaxChunkSize = 512, Overlap = 50 };

    [GlobalSetup]
    public void Setup()
    {
        var embedder = new FakeEmbeddingGenerator(dimensions: 384);
        _semantic = new SemanticChunkingStrategy(embedder, new SemanticChunkingOptions());

        _smallSection = CreateSection(GenerateText(500));
        _largeSection = CreateSection(GenerateText(10_000));
    }

    [Benchmark(Baseline = true)]
    public async Task<int> Semantic_Small()
    {
        int count = 0;
        await foreach (var _ in _semantic.ChunkAsync(_smallSection, _options))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> Semantic_Large()
    {
        int count = 0;
        await foreach (var _ in _semantic.ChunkAsync(_largeSection, _options))
        {
            count++;
        }

        return count;
    }

    private static DocumentSection CreateSection(string text) => new()
    {
        Text = text,
        DocumentId = new DocumentId("bench-doc"),
        SectionIndex = 0,
    };

    private static string GenerateText(int approximateLength)
    {
        const string paragraph = "The quick brown fox jumps over the lazy dog. " +
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit. " +
            "Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.\n\n";

        var sb = new System.Text.StringBuilder(approximateLength + paragraph.Length);
        while (sb.Length < approximateLength)
        {
            sb.Append(paragraph);
        }

        return sb.ToString();
    }

    private sealed class FakeEmbeddingGenerator(int dimensions)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly float[] _fakeEmbedding = new float[dimensions];

        public EmbeddingGeneratorMetadata Metadata { get; } = new("fake");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
                embeddings.Add(new Embedding<float>(_fakeEmbedding));
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
