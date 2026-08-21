using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
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
        var rebuilder = new RaptorTreeRebuilder(behavior, vectorStore);

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
        var rebuilder = new RaptorTreeRebuilder(behavior, vectorStore);

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
}
