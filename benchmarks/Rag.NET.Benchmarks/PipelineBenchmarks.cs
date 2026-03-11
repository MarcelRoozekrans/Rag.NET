using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Parsers;
using Rag.NET.Pipeline;

namespace Rag.NET.Benchmarks;

[MemoryDiagnoser]
public class PipelineBenchmarks
{
    private RagPipeline _pipeline = null!;
    private byte[] _documentData = null!;

    private static readonly DocumentMetadata Metadata = new()
    {
        DocumentId = "bench-doc",
        FileName = "bench.txt",
        ContentType = "text/plain",
    };

    [GlobalSetup]
    public void Setup()
    {
        var vectorStore = new NoOpVectorStore();
        var embedder = new FakeEmbeddingGenerator(dimensions: 384);

        _pipeline = new RagPipeline(
            [new TextDocumentParser()],
            new RecursiveChunkingStrategy(),
            vectorStore,
            embedder,
            chatClient: null,
            new ChunkingOptions { MaxChunkSize = 512, Overlap = 50 });

        _documentData = Encoding.UTF8.GetBytes(GenerateText(50_000));
    }

    [Benchmark]
    public async Task<int> IngestAsync_50KB()
    {
        using var stream = new MemoryStream(_documentData);
        var result = await _pipeline.IngestAsync(stream, Metadata).ConfigureAwait(false);
        return result.ChunksStored;
    }

    private static string GenerateText(int approximateLength)
    {
        const string paragraph = "The quick brown fox jumps over the lazy dog. " +
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit. " +
            "Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.\n\n";

        var sb = new StringBuilder(approximateLength + paragraph.Length);
        while (sb.Length < approximateLength)
        {
            sb.Append(paragraph);
        }

        return sb.ToString();
    }

    private sealed class NoOpVectorStore : Abstractions.IVectorStore
    {
        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, Models.Options.SearchOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SearchResult>>([]);

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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
            {
                embeddings.Add(new Embedding<float>(_fakeEmbedding));
            }

            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
