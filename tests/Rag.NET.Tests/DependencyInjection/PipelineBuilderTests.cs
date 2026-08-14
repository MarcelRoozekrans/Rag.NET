using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class PipelineBuilderTests
{
    // ── IngestionPipelineBuilder ─────────────────────────────────────────

    [Fact]
    public void IngestionBuilder_DefaultContainsAllElevenBehaviors()
    {
        var builder = new IngestionPipelineBuilder();
        var types = builder.GetBehaviorTypes();
        Assert.Equal(11, types.Count);
        Assert.Equal(typeof(StorageBehavior), types[^1]);
    }

    [Fact]
    public void IngestionBuilder_Add_InsertsAfterTarget()
    {
        var builder = new IngestionPipelineBuilder();
        builder.Add<NoOpIngestionBehavior>(after: typeof(ParseBehavior));
        var types = builder.GetBehaviorTypes();
        var parseIdx = types.ToList().IndexOf(typeof(ParseBehavior));
        Assert.Equal(typeof(NoOpIngestionBehavior), types[parseIdx + 1]);
        Assert.Equal(12, types.Count); // 11 defaults + 1 inserted
    }

    [Fact]
    public void IngestionBuilder_Replace_SwapsType()
    {
        var builder = new IngestionPipelineBuilder();
        builder.Replace<EmbeddingBehavior, NoOpIngestionBehavior>();
        var types = builder.GetBehaviorTypes();
        Assert.DoesNotContain(typeof(EmbeddingBehavior), types);
        Assert.Contains(typeof(NoOpIngestionBehavior), types);
        Assert.Equal(11, types.Count); // count unchanged
        // Pipeline order (0-based): Overwrite=0, Parse=1, Chunking=2, LlmMetadataExtraction=3,
        // Metadata=4, TagIngestion=5, ChunkSanitiser=6, ParentDoc=7, Embedding=8,
        // SparseEmbedding=9, Storage=10
        var embeddingIdx = 8;
        Assert.Equal(typeof(NoOpIngestionBehavior), types.ToList()[embeddingIdx]);
    }

    /// <summary>
    /// <c>Add</c> is idempotent, for the reason <c>AddFirst</c> is.
    /// </summary>
    /// <remarks>
    /// Since issue #191, two callers routinely aim at the same slot: a <c>Use*</c> extension
    /// placing the behaviour it registers, and the caller naming that same behaviour in
    /// <c>AddRagNet</c>'s <c>ingestion:</c> delegate the way <c>docs/guide/raptor.md</c> teaches.
    /// <c>Build</c> resolves one singleton per listed type, so inserting twice would run the same
    /// instance at two points of the chain — a second RAPTOR tree per document — with nothing
    /// about the container looking wrong. First insertion wins the position, which is what keeps
    /// the explicit form authoritative: those delegates run before <c>configure</c>.
    /// </remarks>
    [Fact]
    public void IngestionBuilder_Add_CalledTwice_InsertsOnceAtTheFirstPosition()
    {
        var builder = new IngestionPipelineBuilder();
        builder.Add<NoOpIngestionBehavior>(after: typeof(ParseBehavior));
        builder.Add<NoOpIngestionBehavior>(after: typeof(EmbeddingBehavior));

        var types = builder.GetBehaviorTypes();

        Assert.Equal(1, types.Count(t => t == typeof(NoOpIngestionBehavior)));
        Assert.Equal(12, types.Count); // 11 defaults + 1 inserted, not 2
        Assert.Equal(
            types.ToList().IndexOf(typeof(ParseBehavior)) + 1,
            types.ToList().IndexOf(typeof(NoOpIngestionBehavior)));
    }

    // ── RetrievalPipelineBuilder ─────────────────────────────────────────

    [Fact]
    public void RetrievalBuilder_DefaultContainsAllSeventeenBehaviors()
    {
        var builder = new RetrievalPipelineBuilder();
        var types = builder.GetBehaviorTypes();
        Assert.Equal(17, types.Count);
        Assert.Equal(typeof(VectorStoreBehavior), types[^1]);
        Assert.Equal(typeof(EnsembleBehavior), types[^2]);
    }

    [Fact]
    public void RetrievalBuilder_Add_InsertsBeforeTarget()
    {
        var builder = new RetrievalPipelineBuilder();
        builder.Add<NoOpRetrievalBehavior>(before: typeof(VectorStoreBehavior));
        var types = builder.GetBehaviorTypes();
        var vsIdx = types.ToList().IndexOf(typeof(VectorStoreBehavior));
        Assert.Equal(typeof(NoOpRetrievalBehavior), types[vsIdx - 1]);
        Assert.Equal(18, types.Count); // 17 defaults + 1 inserted
    }

    /// <summary>
    /// <c>AddFirst</c> is how satellites bolt an observer onto the outermost position, and layered
    /// composition roots reach it more than once — <c>UseAuditLog</c> and <c>AddRagDiagnostics</c>
    /// both did, each inserting its behaviour twice and so auditing and tracing every retrieval
    /// twice. <c>Add</c>'s caller (<c>UseContextualCompressionInRetrieval</c>) already guards at the
    /// call site; <c>AddFirst</c> guards here instead, once, for every caller.
    /// </summary>
    [Fact]
    public void RetrievalBuilder_AddFirst_CalledTwice_InsertsOnce()
    {
        var builder = new RetrievalPipelineBuilder();
        builder.AddFirst<NoOpRetrievalBehavior>();
        builder.AddFirst<NoOpRetrievalBehavior>();

        var types = builder.GetBehaviorTypes();

        Assert.Equal(1, types.Count(t => t == typeof(NoOpRetrievalBehavior)));
        Assert.Equal(typeof(NoOpRetrievalBehavior), types[0]);
        Assert.Equal(18, types.Count); // 17 defaults + 1 inserted, not 2
    }

    /// <summary>Same guarantee on the retrieval half. See the ingestion case for why.</summary>
    [Fact]
    public void RetrievalBuilder_Add_CalledTwice_InsertsOnceAtTheFirstPosition()
    {
        var builder = new RetrievalPipelineBuilder();
        builder.Add<NoOpRetrievalBehavior>(before: typeof(VectorStoreBehavior));
        builder.Add<NoOpRetrievalBehavior>(before: typeof(RerankingBehavior));

        var types = builder.GetBehaviorTypes();

        Assert.Equal(1, types.Count(t => t == typeof(NoOpRetrievalBehavior)));
        Assert.Equal(18, types.Count); // 17 defaults + 1 inserted, not 2
        Assert.Equal(
            types.ToList().IndexOf(typeof(VectorStoreBehavior)) - 1,
            types.ToList().IndexOf(typeof(NoOpRetrievalBehavior)));
    }

    // ── The seam AddRagNet leaves for other packages ─────────────────────

    /// <summary>
    /// <c>AddRagNet</c> puts <em>both</em> builders in the container, not just the retrieval one.
    /// </summary>
    /// <remarks>
    /// This asymmetry is the whole mechanism behind issue #191. The retrieval builder was
    /// registered and the ingestion builder was not — only the factory that reads it — so a
    /// <c>Use*</c> method in another package could place a retrieval behaviour and had no way at
    /// all to place an ingestion one. Three of them registered ingestion behaviours anyway, and
    /// every one of those registrations was unreachable.
    /// </remarks>
    [Fact]
    public void AddRagNet_RegistersBothPipelineBuilders_SoOtherPackagesCanPlaceBehaviours()
    {
        var services = new ServiceCollection();
        services.AddRagNet();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IngestionPipelineBuilder>());
        Assert.NotNull(provider.GetService<RetrievalPipelineBuilder>());
    }

    /// <summary>
    /// The registered instance is the one the pipeline is built from, so a behaviour placed
    /// through it after registration still ends up in the composed chain.
    /// </summary>
    [Fact]
    public void AddRagNet_RegisteredIngestionBuilder_IsTheOneTheDelegateConfigured()
    {
        var services = new ServiceCollection();
        IngestionPipelineBuilder? fromDelegate = null;
        services.AddRagNet(ingestion: builder => fromDelegate = builder);

        using var provider = services.BuildServiceProvider();

        Assert.Same(fromDelegate, provider.GetRequiredService<IngestionPipelineBuilder>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private sealed class NoOpIngestionBehavior : IIngestionBehavior
    {
        public ValueTask<IngestionResult> HandleAsync(
            IngestionContext ctx, CancellationToken ct,
            Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next) => next(ctx, ct);
    }

    private sealed class NoOpRetrievalBehavior : IRetrievalBehavior
    {
        public ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
            RetrievalContext ctx, CancellationToken ct,
            Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next) => next(ctx, ct);
    }
}
