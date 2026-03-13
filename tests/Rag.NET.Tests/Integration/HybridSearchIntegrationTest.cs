using System.Text;
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Xunit;

namespace Rag.NET.Tests.Integration;

/// <summary>
/// End-to-end integration tests that wire up a real <see cref="RagPipeline"/> using
/// in-memory/fake infrastructure and exercise the hybrid search path including
/// ingestion, retrieval, and deletion.
/// </summary>
public class HybridSearchIntegrationTest
{
    // ──────────────────────────────────────────────────────────────────
    // Fakes / infrastructure
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Trivial parser that returns a single section with the entire stream content as text.
    /// </summary>
    private sealed class PassThroughParser : IDocumentParser
    {
        public bool CanParse(string contentType) => true;

        public async IAsyncEnumerable<DocumentSection> ParseAsync(
            Stream stream,
            DocumentMetadata metadata,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            yield return new DocumentSection { Text = text, DocumentId = metadata.DocumentId, SectionIndex = 0 };
        }
    }

    /// <summary>
    /// Trivial chunker that returns the whole section as one chunk.
    /// </summary>
    private sealed class SingleChunkStrategy : IChunkingStrategy
    {
        public async IAsyncEnumerable<TextChunk> ChunkAsync(
            DocumentSection section,
            ChunkingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new TextChunk { Text = section.Text, DocumentId = section.DocumentId, ChunkIndex = 0 };
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Test helpers
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="RagPipeline"/> that uses the pass-through parser/chunker,
    /// a substituted embedder that always returns a fixed vector, and a substituted
    /// vector store that always returns empty dense results (forcing BM25 fallback).
    /// </summary>
    private static (RagPipeline pipeline, IVectorStore vectorStore, IEmbeddingGenerator<string, Embedding<float>> embedder)
        BuildPipeline()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        // Dense search always returns empty so BM25 is the only signal
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(System.Threading.Tasks.Task.FromResult<IReadOnlyList<SearchResult>>([]));

        // Embedder always returns a unit vector (content does not matter for BM25 path)
        var fixedEmbedding = new Embedding<float>(new float[] { 1f, 0f, 0f });
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([fixedEmbedding]));

        var pipeline = new RagPipeline(
            [new PassThroughParser()],
            new SingleChunkStrategy(),
            vectorStore,
            embedder,
            chatClient: null,
            new ChunkingOptions());

        return (pipeline, vectorStore, embedder);
    }

    private static async Task IngestTextAsync(RagPipeline pipeline, string documentId, string text, CancellationToken ct)
    {
        var metadata = new DocumentMetadata
        {
            DocumentId = documentId,
            FileName = $"{documentId}.txt",
            ContentType = "text/plain",
        };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await pipeline.IngestAsync(stream, metadata, cancellationToken: ct).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────
    // Integration tests
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 1. Ingest several documents with distinct content.
    /// 2. Call RetrieveAsync with UseHybridSearch = true.
    /// 3. Verify results are returned and the top result is relevant to the query.
    /// </summary>
    [Fact]
    public async Task HybridSearch_ReturnsRelevantTopResult()
    {
        var (pipeline, _, _) = BuildPipeline();
        var ct = TestContext.Current.CancellationToken;

        await IngestTextAsync(pipeline, "doc-astronomy", "The Milky Way is a barred spiral galaxy containing billions of stars.", ct);
        await IngestTextAsync(pipeline, "doc-cooking", "Risotto requires constant stirring and warm broth added gradually.", ct);
        await IngestTextAsync(pipeline, "doc-programming", "A binary search tree stores keys in sorted order for fast lookup.", ct);

        var results = await pipeline.RetrieveAsync(
            "galaxy stars",
            new RetrievalOptions { UseHybridSearch = true, TopK = 5 },
            ct);

        Assert.NotEmpty(results);
        // The astronomy document should be ranked first — it contains both "galaxy" and "stars"
        Assert.Equal("doc-astronomy", results[0].Chunk.DocumentId);
    }

    /// <summary>
    /// 1. Ingest two documents, then delete one.
    /// 2. Verify the deleted document no longer appears in hybrid retrieval results.
    /// </summary>
    [Fact]
    public async Task HybridSearch_AfterDelete_DeletedDocNotReturned()
    {
        var (pipeline, _, _) = BuildPipeline();
        var ct = TestContext.Current.CancellationToken;

        await IngestTextAsync(pipeline, "doc-keep", "photosynthesis converts sunlight into energy in plants", ct);
        await IngestTextAsync(pipeline, "doc-delete", "photosynthesis is the process used by plants and algae", ct);

        // Delete one document
        await pipeline.DeleteAsync("doc-delete", ct);

        var results = await pipeline.RetrieveAsync(
            "photosynthesis",
            new RetrievalOptions { UseHybridSearch = true, TopK = 10 },
            ct);

        // doc-delete must not appear
        Assert.DoesNotContain(results, r =>
            string.Equals(r.Chunk.DocumentId, "doc-delete", StringComparison.Ordinal));

        // doc-keep should still appear
        Assert.Contains(results, r =>
            string.Equals(r.Chunk.DocumentId, "doc-keep", StringComparison.Ordinal));
    }
}
