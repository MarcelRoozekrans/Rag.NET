using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Rag.NET.Search;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class RetrievalBehaviorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SearchResult MakeResult(string docId, int chunkIndex, double score) =>
        new()
        {
            Chunk = new TextChunk { Text = $"{docId}-{chunkIndex}", DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
            Score = score
        };

    private static RetrievalContext MakeCtx(RetrievalOptions options) =>
        new() { Query = "test query", Options = options };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        NextReturning(IReadOnlyList<SearchResult> results) =>
        (_, _) => ValueTask.FromResult(results);

    // ── LostInTheMiddleBehavior ───────────────────────────────────────────────

    [Fact]
    public async Task LostInTheMiddle_WhenFlagFalse_ReturnsResultsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
            MakeResult("doc-3", 0, 0.7),
        };

        var sut = new LostInTheMiddleBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseLostInTheMiddleReordering = false });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    [Fact]
    public async Task LostInTheMiddle_WhenFlagTrue_ReordersResults()
    {
        var ct = TestContext.Current.CancellationToken;
        // With 4 results: even-indexed (0,2) go left, odd-indexed (1,3) go right
        // Input: doc-1(0.9), doc-2(0.8), doc-3(0.7), doc-4(0.6)
        // Expected: doc-1, doc-3, doc-4, doc-2
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
            MakeResult("doc-3", 0, 0.7),
            MakeResult("doc-4", 0, 0.6),
        };

        var sut = new LostInTheMiddleBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseLostInTheMiddleReordering = true });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Equal(4, output.Count);
        Assert.Equal("doc-1", output[0].Chunk.DocumentId);
        Assert.Equal("doc-3", output[1].Chunk.DocumentId);
        Assert.Equal("doc-4", output[2].Chunk.DocumentId);
        Assert.Equal("doc-2", output[3].Chunk.DocumentId);
    }

    // ── MmrBehavior ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Mmr_WhenFlagFalse_PassesThroughToNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
        };

        // Embedder is null! — must not be called when UseMmr = false
        var sut = new MmrBehavior { Embedder = null! };
        var ctx = MakeCtx(new RetrievalOptions { UseMmr = false });

        var nextCalled = false;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (_, _) =>
            {
                nextCalled = true;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>(results);
            };

        var output = await sut.HandleAsync(ctx, ct, next);

        Assert.True(nextCalled);
        Assert.Same(results, output);
    }

    // ── RedundancyFilterBehavior ──────────────────────────────────────────────

    [Fact]
    public async Task RedundancyFilter_WhenFlagFalse_ReturnsNextResultsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
        };

        // Embedder is null! — must not be called when UseRedundancyFilter = false
        var sut = new RedundancyFilterBehavior { Embedder = null! };
        var ctx = MakeCtx(new RetrievalOptions { UseRedundancyFilter = false });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    // ── ParentDocumentRetrievalBehavior ───────────────────────────────────────

    [Fact]
    public async Task ParentDocument_WhenStoreNull_ReturnsNextResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        var sut = new ParentDocumentRetrievalBehavior { ParentStore = null };
        var ctx = MakeCtx(new RetrievalOptions { UseParentDocument = true });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    [Fact]
    public async Task ParentDocument_WhenFlagFalse_ReturnsNextResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        var store = new InMemoryParentChunkStore();
        var sut = new ParentDocumentRetrievalBehavior { ParentStore = store };
        var ctx = MakeCtx(new RetrievalOptions { UseParentDocument = false });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    // ── ResultCacheBehavior ───────────────────────────────────────────────────

    [Fact]
    public async Task ResultCache_WhenFlagFalse_CallsNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        // Cache and CachingOptions are null — flag-false short-circuits before touching them
        var sut = new ResultCacheBehavior { Cache = null, CachingOptions = null };
        var ctx = MakeCtx(new RetrievalOptions { UseCacheResult = false });

        var nextCalled = false;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (_, _) =>
            {
                nextCalled = true;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>(results);
            };

        var output = await sut.HandleAsync(ctx, ct, next);

        Assert.True(nextCalled);
        Assert.Same(results, output);
    }

    // ── RerankingBehavior ─────────────────────────────────────────────────────

    [Fact]
    public async Task Reranking_WhenRerankerNull_ReturnsNextResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
        };

        var sut = new RerankingBehavior { Reranker = null };
        var ctx = MakeCtx(new RetrievalOptions { UseReranking = true });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    [Fact]
    public async Task Reranking_WhenFlagFalse_ReturnsNextResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        var reranker = Substitute.For<IReranker>();
        var sut = new RerankingBehavior { Reranker = reranker };
        var ctx = MakeCtx(new RetrievalOptions { UseReranking = false });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
        await reranker.DidNotReceive().RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A failing reranker degrades to the caller's <c>TopK</c>, not to the candidate pool.
    /// <para>
    /// The failure path returned the candidate list untouched, and that list is
    /// <c>CandidateCount</c> long — <c>TopK * 3</c> by default. So a reranker throwing did not
    /// degrade the request, it <b>widened</b> it: asking for 5 chunks and getting 15, with only a
    /// warning about reranking to explain it (issue #94).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Reranking_WhenRerankerThrows_FallsBackToTopKRatherThanTheWholeCandidatePool()
    {
        var ct = TestContext.Current.CancellationToken;
        var candidates = new List<SearchResult>();
        for (var i = 0; i < 15; i++)
            candidates.Add(MakeResult($"doc-{i}", 0, 1.0 - (i * 0.01)));

        var reranker = Substitute.For<IReranker>();
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RerankResult>>>(_ => throw new InvalidOperationException("reranker down"));

        var sut = new RerankingBehavior { Reranker = reranker };
        var ctx = MakeCtx(new RetrievalOptions { UseReranking = true, TopK = 5 });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(candidates));

        Assert.Equal(5, output.Count);
    }

    /// <summary>
    /// A reranker returning fewer than <c>TopK</c> is reported rather than silently accepted.
    /// <para>
    /// <c>CohereRerankerOptions.TopN</c> defaulted to 5, so a caller asking for 20 got 5 chunks:
    /// the behaviour's <c>Take(TopK)</c> was a no-op over a list the reranker had already cut, and
    /// nothing said so. The ONNX reranker returns every candidate, so the same configuration gave
    /// different answer sizes depending on which reranker was registered.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Reranking_WhenRerankerReturnsFewerThanTopK_LogsRatherThanSilentlyShrinking()
    {
        var ct = TestContext.Current.CancellationToken;
        var candidates = new List<SearchResult>();
        for (var i = 0; i < 30; i++)
            candidates.Add(MakeResult($"doc-{i}", 0, 1.0 - (i * 0.01)));

        // The shape a capped reranker produces: fewer results than the caller asked for.
        var capped = new List<RerankResult>();
        for (var i = 0; i < 5; i++)
            capped.Add(new RerankResult { SearchResult = candidates[i], RelevanceScore = 1.0 - (i * 0.01) });

        var reranker = Substitute.For<IReranker>();
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RerankResult>>(capped));

        var logger = new RecordingLogger();
        var sut = new RerankingBehavior { Reranker = reranker };
        var ctx = MakeCtx(new RetrievalOptions { UseReranking = true, TopK = 10 }) with { Logger = logger };

        var output = await sut.HandleAsync(ctx, ct, NextReturning(candidates));

        Assert.Equal(5, output.Count);
        Assert.Contains(
            logger.Messages,
            message => message.Contains("returned 5 results for a TopK of 10", StringComparison.Ordinal));
    }

    /// <summary>Captures formatted log messages so a warning can be asserted on rather than assumed.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    // ── ContextBudgetBehavior ─────────────────────────────────────────────────

    /// <summary>No budget configured leaves the result set exactly as it was.</summary>
    [Fact]
    public async Task ContextBudget_WhenUnset_ReturnsResultsUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };

        var sut = new ContextBudgetBehavior();
        var ctx = MakeCtx(new RetrievalOptions());

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    /// <summary>
    /// The budget drops from the tail, so the chunks that survive are the highest-ranked ones.
    /// <para>
    /// TopK bounds how many chunks come back, never how long they are — so a corpus rechunked
    /// from 500 to 4,000 characters silently multiplied the prompt at the same TopK, with no
    /// error until the model rejected the request (issue #85).
    /// </para>
    /// </summary>
    [Fact]
    public async Task ContextBudget_WhenOverBudget_KeepsTheHighestRankedChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            WithText("doc-1", "alpha beta gamma delta", 0.9),
            WithText("doc-2", "epsilon zeta eta theta", 0.8),
            WithText("doc-3", "iota kappa lambda mu", 0.7),
        };

        // Enough for roughly the first two chunks, not all three.
        var sut = new ContextBudgetBehavior();
        var ctx = MakeCtx(new RetrievalOptions { MaxContextTokens = 8 });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.True(output.Count < results.Count, "nothing was dropped, so the budget did nothing");
        Assert.Equal("doc-1", output[0].Chunk.DocumentId);
        Assert.DoesNotContain(output, r => string.Equals(r.Chunk.DocumentId, "doc-3", StringComparison.Ordinal));
    }

    /// <summary>A set already inside the budget is returned as-is, with nothing dropped.</summary>
    [Fact]
    public async Task ContextBudget_WhenWithinBudget_DropsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { WithText("doc-1", "short", 0.9) };

        var sut = new ContextBudgetBehavior();
        var ctx = MakeCtx(new RetrievalOptions { MaxContextTokens = 1000 });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    /// <summary>
    /// The budget runs <b>inside</b> LostInTheMiddle in the behaviour chain, so ranking decides
    /// what survives and reordering only arranges the survivors.
    /// <para>
    /// The other order drops whichever chunk ends up last, and lost-in-the-middle deliberately
    /// puts the weakest chunk in the middle and strong ones at both ends — so the dropped chunk
    /// would be a mid-ranked one, chosen by position rather than by rank. This pins the registered
    /// order rather than the reasoning.
    /// </para>
    /// </summary>
    [Fact]
    public void ContextBudget_RunsInsideLostInTheMiddle_SoRankDecidesWhatSurvives()
    {
        var order = new RetrievalPipelineBuilder().GetBehaviorTypes();

        var reorder = IndexOfBehavior(order, typeof(LostInTheMiddleBehavior));
        var budget = IndexOfBehavior(order, typeof(ContextBudgetBehavior));

        Assert.True(reorder >= 0 && budget >= 0, "both behaviours must be registered");
        Assert.True(
            budget > reorder,
            "ContextBudgetBehavior must sit inside LostInTheMiddleBehavior — later in the list is "
            + "further in, so the budget trims a settled ranking and the reorder then applies to "
            + "the survivors. Outside it, the budget would drop by position after reordering.");
    }

    /// <summary>The position of a behaviour in the registered chain, or -1.</summary>
    /// <param name="order">The registered behaviour types, outermost first.</param>
    /// <param name="behavior">The behaviour to locate.</param>
    /// <returns>Its index, or -1 when it is not registered.</returns>
    private static int IndexOfBehavior(IReadOnlyList<Type> order, Type behavior)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == behavior)
            {
                return i;
            }
        }

        return -1;
    }

    private static SearchResult WithText(string docId, string text, double score) =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = 0 },
            Score = score,
        };

    // ── HydeBehavior ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Hyde_WhenGeneratorNull_ReturnsNextResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        var sut = new HydeBehavior { HydeGenerator = null };
        var ctx = MakeCtx(new RetrievalOptions { UseHyde = true });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    [Fact]
    public async Task Hyde_WhenEnabled_PassesHypotheticalDocAsEmbeddingOverride()
    {
        var ct = TestContext.Current.CancellationToken;
        const string hypotheticalDoc = "This is the hypothetical document.";
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        var generator = Substitute.For<IHypotheticalDocumentGenerator>();
        generator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(hypotheticalDoc));

        var sut = new HydeBehavior { HydeGenerator = generator };
        var ctx = MakeCtx(new RetrievalOptions { UseHyde = true });

        string? capturedOverride = null;
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next =
            (innerCtx, _) =>
            {
                capturedOverride = innerCtx.Options.EmbeddingTextOverride;
                return ValueTask.FromResult<IReadOnlyList<SearchResult>>(results);
            };

        var output = await sut.HandleAsync(ctx, ct, next);

        Assert.Equal(hypotheticalDoc, capturedOverride);
        Assert.Same(results, output);
    }

    // ── MultiQueryBehavior ────────────────────────────────────────────────────

    [Fact]
    public async Task MultiQuery_WhenExpanderNull_ReturnsNextResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
        };

        var sut = new MultiQueryBehavior { QueryExpander = null };
        var ctx = MakeCtx(new RetrievalOptions { UseMultiQuery = true });

        var output = await sut.HandleAsync(ctx, ct, NextReturning(results));

        Assert.Same(results, output);
    }

    // ── VectorStoreBehavior ───────────────────────────────────────────────────

    [Fact]
    public async Task VectorStore_WhenDenseSearch_CallsVectorStoreAndReturnsResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var expected = MakeResult("doc-1", 0, 0.95);

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));

        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { expected });

        var sut = new VectorStoreBehavior
        {
            VectorStore = vectorStore,
            Embedder = embedder,
        };

        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = false });
        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("Terminal behavior must not call next"));

        Assert.Single(output);
        Assert.Equal(0.95, output[0].Score);
    }

    [Fact]
    public async Task VectorStore_WhenHybridSearch_PerformsDenseOnlySearch()
    {
        var ct = TestContext.Current.CancellationToken;
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var queryEmbedding = new Embedding<float>(new float[] { 0.1f, 0.2f });
        var expected = MakeResult("doc-1", 0, 0.95);

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([queryEmbedding]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult> { expected });

        var sut = new VectorStoreBehavior
        {
            VectorStore = vectorStore,
            Embedder = embedder,
        };
        var ctx = MakeCtx(new RetrievalOptions { UseHybridSearch = true });

        var output = await sut.HandleAsync(ctx, ct, (_, _) => throw new InvalidOperationException("must not call next"));

        Assert.Single(output);
        await vectorStore.Received(1).SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>());
    }
}
