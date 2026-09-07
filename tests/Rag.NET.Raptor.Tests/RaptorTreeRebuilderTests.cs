using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Search;
using Rag.NET.Raptor.Store;
using NSubstitute;
using Xunit;

namespace Rag.NET.Raptor.Tests;

/// <summary>
/// <see cref="RaptorTreeRebuilder"/> is the on-demand counterpart to the corpus-scope ingest-time
/// growth threshold (Task 4): the way to say "make the tree current now" — after a bulk load,
/// before measuring, or on a schedule.
/// </summary>
public class RaptorTreeRebuilderTests
{
    private readonly RaptorTestContext _helpers = new();

    /// <remarks>
    /// The assertion that matters: the delete must precede the store, because a rebuild producing
    /// fewer summaries than last time would otherwise leave the surplus behind as orphans that
    /// retrieval could still return.
    /// </remarks>
    [Fact]
    public async Task Rebuild_DeletesThePreviousTreeBeforeStoringTheNewOne()
    {
        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);

        var vectorStore = Substitute.For<IVectorStore>();
        await using var leafStore = new SqliteRaptorLeafStore(":memory:");
        await leafStore.InitializeAsync(TestContext.Current.CancellationToken);
        await leafStore.AddLeavesAsync(TwentyLeaves(), TestContext.Current.CancellationToken);

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
        var rebuilder = new RaptorTreeRebuilder(behavior, vectorStore, new InMemoryBm25Index());

        var count = await rebuilder.RebuildAsync(TestContext.Current.CancellationToken);

        Assert.True(count > 0);
        Received.InOrder(() =>
        {
            _ = vectorStore.DeleteByDocumentIdAsync(RaptorCorpusDocumentId.Value, Arg.Any<CancellationToken>());
            _ = vectorStore.StoreAsync(Arg.Any<IReadOnlyList<EmbeddedChunk>>(), Arg.Any<CancellationToken>());
        });
    }

    /// <remarks>
    /// Without the baseline reset in <c>RaptorIngestionBehavior.BuildCorpusTreeNowAsync</c>,
    /// <c>_leavesAtLastBuild</c> would still be -1 after the rebuild, the sentinel would report
    /// "build now" for the very next ingest, and this test fails — which is the whole reason the
    /// reset lives in that method.
    /// </remarks>
    [Fact]
    public async Task Rebuild_ResetsTheGrowthBaseline_SoLaterIngestsDebounceFromTheRebuiltState()
    {
        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);

        var vectorStore = Substitute.For<IVectorStore>();
        await using var leafStore = new SqliteRaptorLeafStore(":memory:");
        await leafStore.InitializeAsync(TestContext.Current.CancellationToken);
        await leafStore.AddLeavesAsync(TwentyLeaves(), TestContext.Current.CancellationToken);

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus, CorpusGrowthThreshold = 0.50 };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
        var rebuilder = new RaptorTreeRebuilder(behavior, vectorStore, new InMemoryBm25Index());

        await rebuilder.RebuildAsync(TestContext.Current.CancellationToken);
        var callsAfterRebuild = _helpers.ChatClient.ReceivedCalls().Count();

        // The rebuild set the baseline to 20. Two more leaves is 22, under the 30 the
        // 50% threshold requires, so ingesting them must not trigger another build.
        var next = _helpers.CreateContext(chunkCount: 2, documentId: "doc-late");
        await behavior.HandleAsync(next, CancellationToken.None, static (_, _) => ValueTask.FromResult(new IngestionResult { DocumentId = new DocumentId("doc-late"), ChunksStored = 0 }));

        Assert.Equal(callsAfterRebuild, _helpers.ChatClient.ReceivedCalls().Count());
    }

#pragma warning disable HLQ013 // Use foreach — need index-based assignment
    // Four tight, well-separated blobs rather than twenty uniform random vectors. Uniform noise
    // has no cluster structure, so once SelectK stopped isolating every point into its own
    // component (#333) BIC read all twenty as a single Gaussian and the rebuild produced no
    // summaries at all — the count > 0 precondition below then failed before the ordering
    // assertion it exists to protect could ever run.
    private static IReadOnlyList<RaptorLeaf> TwentyLeaves()
    {
        var rng = new Random(Seed: 42);
        var leaves = new List<RaptorLeaf>(20);
        for (var i = 0; i < 20; i++)
        {
            var vector = new float[8];
            for (var d = 0; d < vector.Length; d++)
                vector[d] = (i / 5) + (float)(rng.NextDouble() * 0.1);

            leaves.Add(new RaptorLeaf($"doc-{i / 4}", i % 4, $"leaf text {i}", vector));
        }

        return leaves;
    }
#pragma warning restore HLQ013

    /// <summary>
    /// A rebuild replaces the tree's BM25 postings as well as its vectors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is #487.</b> <c>RebuildAsync</c> wrote through <c>IVectorStore</c> and never touched
    /// <c>IBm25Index</c>, so after a rebuild the two stores disagreed: the vector store held the
    /// new tree while BM25 held whatever the ingest path last wrote. The rebuilt summaries were
    /// invisible to keyword and hybrid search and the stale ones were still being returned — and
    /// the guide offers <c>RebuildAsync</c> as the way to force a tree current.
    /// </para>
    /// <para>
    /// <b>It could not be fixed without #490 first.</b> Writing BM25 needs doc ids, and the only
    /// allocator was a counter private to <c>PipelineIngestor</c>; both rebuilders stubbed it as
    /// <c>() =&gt; 0</c>, which would have indexed the first summary and silently dropped every
    /// other one. The index allocates now, so a caller with no ingest in flight can write to it.
    /// </para>
    /// <para>
    /// Remove-then-add, for the reason the vector store is deleted first: clustering is not stable
    /// across runs, so a shorter tree must not leave the previous run's surplus behind.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RebuildAsync_ReplacesTheTreesBm25Postings()
    {
        var ct = TestContext.Current.CancellationToken;
        _helpers.SetupChatClient("a summary");
        _helpers.SetupEmbedder(dims: 8);

        var vectorStore = Substitute.For<IVectorStore>();
        using var bm25 = new InMemoryBm25Index();

        // A previous tree's postings, which the rebuild must replace rather than accumulate.
        bm25.Add(new TextChunk
        {
            Text = "stale summary quokka",
            DocumentId = new DocumentId(RaptorCorpusDocumentId.Value),
            ChunkIndex = 0,
        });
        Assert.Single(bm25.Search("quokka", topK: 10));

        await using var leafStore = new SqliteRaptorLeafStore(":memory:");
        await leafStore.InitializeAsync(ct);
        await leafStore.AddLeavesAsync(TwentyLeaves(), ct);

        var options = new RaptorOptions { TreeScope = RaptorTreeScope.Corpus };
        var behavior = new RaptorIngestionBehavior(_helpers.ChatClient, _helpers.Embedder, options, leafStore);
        var rebuilder = new RaptorTreeRebuilder(behavior, vectorStore, bm25);

        var count = await rebuilder.RebuildAsync(ct);
        Assert.True(count > 0);

        // The stale posting is gone...
        Assert.Empty(bm25.Search("quokka", topK: 10));

        // ...and the rebuilt tree is lexically searchable, which it never was before.
        Assert.NotEmpty(bm25.Search("summary", topK: 10));
    }
}
