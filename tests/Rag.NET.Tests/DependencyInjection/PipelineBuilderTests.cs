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
