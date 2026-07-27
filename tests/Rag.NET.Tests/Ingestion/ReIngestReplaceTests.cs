using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Ingestion;

/// <summary>
/// Re-ingesting a document must <em>replace</em> what the previous ingest wrote, not append to
/// it. Before the fix, <c>StorageBehavior</c> called <c>Bm25Index.Add</c> with a fresh doc id
/// on every ingest and removal happened only under <c>IngestionOptions.Overwrite</c>, so the
/// second ingest of the same document produced a second complete set of BM25 postings.
/// See <c>docs/plans/2026-07-27-service-bus-ingestion-design.md</c> §1.
/// </summary>
public class ReIngestReplaceTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every chunk embeds to the same unit vector, so a vector search with that same vector
    /// scores 1.0 against everything stored — which makes "how many chunks does the store
    /// still hold for this document?" answerable without an enumeration API.
    /// </summary>
    private sealed class ConstantEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public static readonly float[] Vector = [1f, 0f];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values) embeddings.Add(new Embedding<float>(Vector));
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        /// <summary>Nothing to release — the fake holds no unmanaged or disposable state.</summary>
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Records the vector-store calls the ingestion pipeline makes. Hand-written rather than
    /// substituted so the <see cref="Task"/>-returning members stay allocation-honest and the
    /// call log is ordered.
    /// </summary>
    private sealed class RecordingVectorStore : IVectorStore
    {
        public List<int> StoreCalls { get; } = [];
        public List<string> Deletes { get; } = [];

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
        {
            StoreCalls.Add(chunks.Count);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([]);

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
        {
            Deletes.Add(documentId);
            return Task.CompletedTask;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ServiceProvider BuildHost(IVectorStore vectorStore, ChunkingOptions? chunking = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(vectorStore);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new ConstantEmbedder());
        if (chunking is not null) services.AddSingleton(chunking);
        services.AddRagNet();
        return services.BuildServiceProvider();
    }

    private static async Task<Result<IngestionResult, RagError>> TryIngestAsync(
        IIngestor ingestor, string documentId, string text, CancellationToken ct,
        IngestionOptions? options = null, string fileName = "{0}.txt", string contentType = "text/plain")
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        return await ingestor.IngestAsync(
            stream,
            new DocumentMetadata
            {
                DocumentId = new DocumentId(documentId),
                FileName = string.Format(CultureInfo.InvariantCulture, fileName, documentId),
                ContentType = contentType,
            },
            options,
            cancellationToken: ct);
    }

    private static async Task<int> IngestAsync(IIngestor ingestor, string documentId, string text, CancellationToken ct)
    {
        var result = await TryIngestAsync(ingestor, documentId, text, ct);
        Assert.True(result.IsSuccess, "ingestion should succeed");
        return result.Value.ChunksStored;
    }

    private static async Task<int> CountVectorChunksAsync(
        InMemoryVectorStore store, string documentId, CancellationToken ct)
    {
        var hits = await store.SearchAsync(
            ConstantEmbedder.Vector, new SearchOptions { TopK = 1000 }, ct);

        var count = 0;
        foreach (var hit in hits)
        {
            if (string.Equals(hit.Chunk.DocumentId.Value, documentId, StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    /// <summary>One paragraph, sized so the 512-char chunker emits exactly one chunk per paragraph.</summary>
    private static string MakeParagraph(int i) =>
        $"quokka paragraph number {i}. " + string.Join(" ", Enumerable.Repeat("telemetry ingestion pipeline text", 12));

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The defect this phase exists to fix: the webhook path never sets
    /// <see cref="IngestionOptions"/>, so a replayed document used to land twice in the
    /// keyword index.
    /// </summary>
    [Fact]
    public async Task Ingest_SameDocumentTwice_DoesNotDuplicateBm25Postings()
    {
        var ct = TestContext.Current.CancellationToken;
        using var vectorStore = new InMemoryVectorStore();
        await using var sp = BuildHost(vectorStore);
        var ingestor = sp.GetRequiredService<IIngestor>();
        var bm25 = sp.GetRequiredService<IBm25Index>();

        const string Text = "quokka telemetry arrives on the ingestion path";
        await IngestAsync(ingestor, "doc-1", Text, ct);
        await IngestAsync(ingestor, "doc-1", Text, ct);

        var hits = bm25.Search("quokka", topK: 10);

        Assert.Single(hits);
        Assert.Equal("doc-1", hits[0].chunk.DocumentId.Value);
    }

    /// <summary>
    /// Pins that the fix did <em>not</em> touch vector-store semantics: without
    /// <see cref="IngestionOptions.Overwrite"/> the store is still upserted into and
    /// <c>DeleteByDocumentIdAsync</c> is still not called. Making that delete unconditional
    /// would change what <c>Overwrite</c> means for every existing caller and is explicitly
    /// out of scope — <c>docs/plans/2026-07-27-service-bus-ingestion-design.md</c> §1
    /// ("Recorded, not fixed") and "Out of scope".
    /// </summary>
    [Fact]
    public async Task Ingest_SameDocumentTwice_VectorStoreStillUpserts()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = new RecordingVectorStore();
        await using var sp = BuildHost(vectorStore);
        var ingestor = sp.GetRequiredService<IIngestor>();

        const string Text = "quokka telemetry arrives on the ingestion path";
        await IngestAsync(ingestor, "doc-1", Text, ct);
        await IngestAsync(ingestor, "doc-1", Text, ct);

        Assert.Equal(2, vectorStore.StoreCalls.Count);
        Assert.All(vectorStore.StoreCalls, count => Assert.True(count > 0, "each ingest stores chunks"));
        Assert.Empty(vectorStore.Deletes);
    }

    /// <summary>
    /// RECORDED LIMITATION, not a bug to fix here. The vector store upserts on
    /// <c>(documentId, chunkIndex)</c> and nothing deletes the tail, so re-ingesting a
    /// <em>shorter</em> version of a document leaves the surplus chunks of the longer version
    /// stranded and still retrievable — the design doc's example is a 9-chunk document replaced
    /// by a 5-chunk one leaving chunks 5-8 behind; here nine paragraphs are replaced by one and
    /// the tail survives just the same. BM25 is a clean replace; vectors are a partial one.
    /// This test exists so that asymmetry lives in the suite and not only in prose:
    /// see <c>docs/plans/2026-07-27-service-bus-ingestion-design.md</c> §1
    /// ("Recorded, not fixed: orphan tail chunks"). If a later phase makes delete-before-insert
    /// unconditional, this test is the one that should start failing.
    /// </summary>
    [Fact]
    public async Task Ingest_ShorterDocument_LeavesOrphanTailChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var chunking = new ChunkingOptions { MaxChunkSize = 512, Overlap = 50 };
        var longText = string.Join("\n\n", Enumerable.Range(0, 9).Select(MakeParagraph));
        var shortText = MakeParagraph(0);

        // Control: a host that only ever saw the shorter document. Its stores are what a true
        // replace would leave behind, so nothing here depends on the chunker's exact output.
        using var controlStore = new InMemoryVectorStore();
        await using var controlSp = BuildHost(controlStore, chunking);
        var shortChunks = await IngestAsync(controlSp.GetRequiredService<IIngestor>(), "doc-1", shortText, ct);
        var controlBm25Hits = controlSp.GetRequiredService<IBm25Index>().Search("paragraph", topK: 1000).Count;
        var controlVectorChunks = await CountVectorChunksAsync(controlStore, "doc-1", ct);

        // Subject: the long document replaced by the short one.
        using var vectorStore = new InMemoryVectorStore();
        await using var sp = BuildHost(vectorStore, chunking);
        var ingestor = sp.GetRequiredService<IIngestor>();
        var longChunks = await IngestAsync(ingestor, "doc-1", longText, ct);
        _ = await IngestAsync(ingestor, "doc-1", shortText, ct);

        Assert.True(longChunks > shortChunks, $"expected the replacement to be shorter ({longChunks} → {shortChunks})");

        // BM25: a clean replace — indistinguishable from having only ever seen the short document.
        Assert.Equal(controlBm25Hits, sp.GetRequiredService<IBm25Index>().Search("paragraph", topK: 1000).Count);

        // Vectors: a partial replace — the long document's tail survives, so the store still
        // holds one entry per (documentId, chunkIndex) of the LONGER document.
        var subjectVectorChunks = await CountVectorChunksAsync(vectorStore, "doc-1", ct);
        Assert.Equal(longChunks, subjectVectorChunks);
        Assert.True(
            subjectVectorChunks > controlVectorChunks,
            $"orphan tail chunks are expected to survive ({subjectVectorChunks} vs {controlVectorChunks})");
    }

    /// <summary>
    /// Pins why <see cref="OverwriteBehavior"/> keeps its own BM25/data-manager removals even
    /// though <see cref="StorageBehavior"/> now removes unconditionally: <c>Overwrite</c> promises
    /// the document is gone up front <em>whatever happens next</em>, and <c>StorageBehavior</c>
    /// never runs when the replacement fails to parse. Delete these removals and this test fails.
    /// </summary>
    [Fact]
    public async Task Ingest_OverwriteThenUnparseableContent_LeavesNothingInBm25()
    {
        var ct = TestContext.Current.CancellationToken;
        using var vectorStore = new InMemoryVectorStore();
        await using var sp = BuildHost(vectorStore);
        var ingestor = sp.GetRequiredService<IIngestor>();
        var bm25 = sp.GetRequiredService<IBm25Index>();

        await IngestAsync(ingestor, "doc-1", "quokka telemetry arrives on the ingestion path", ct);
        Assert.Single(bm25.Search("quokka", topK: 10));

        var replacement = await TryIngestAsync(
            ingestor, "doc-1", "irrelevant", ct,
            options: new IngestionOptions { Overwrite = true },
            fileName: "{0}.bin", contentType: "application/x-no-such-parser");

        Assert.False(replacement.IsSuccess, "no parser should be found for this content type");
        Assert.Empty(bm25.Search("quokka", topK: 10));
    }

    /// <summary>
    /// The same guarantee on the other path <see cref="StorageBehavior"/> never reaches:
    /// whitespace-only content chunks to nothing, so <c>ChunkingBehavior</c> short-circuits
    /// before storage.
    /// </summary>
    [Fact]
    public async Task Ingest_OverwriteThenEmptyContent_LeavesNothingInBm25()
    {
        var ct = TestContext.Current.CancellationToken;
        using var vectorStore = new InMemoryVectorStore();
        await using var sp = BuildHost(vectorStore);
        var ingestor = sp.GetRequiredService<IIngestor>();
        var bm25 = sp.GetRequiredService<IBm25Index>();

        await IngestAsync(ingestor, "doc-1", "quokka telemetry arrives on the ingestion path", ct);
        Assert.Single(bm25.Search("quokka", topK: 10));

        var replacement = await TryIngestAsync(
            ingestor, "doc-1", "   \n  ", ct, options: new IngestionOptions { Overwrite = true });

        Assert.True(replacement.IsSuccess);
        Assert.Equal(0, replacement.Value.ChunksStored);
        Assert.Empty(bm25.Search("quokka", topK: 10));
    }
}
