using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Raptor.Tests;

/// <summary>
/// <c>UseRaptor()</c> on its own actually enables RAPTOR (issue #191).
/// </summary>
/// <remarks>
/// <para>
/// It used to register <see cref="RaptorIngestionBehavior"/> and
/// <see cref="RaptorRetrievalBehavior"/> as singletons and stop there. Neither type is in either
/// default pipeline, and <c>Build</c> only ever resolves the types the pipeline lists, so a caller
/// who wrote <c>rag.UseRaptor()</c> and nothing else got a plain vector pipeline: no tree, no
/// error, no log line. The method's own summary said it registers both behaviours "into the
/// pipeline", which was the one thing it did not do.
/// </para>
/// <para>
/// The existing suite asserted the <em>registrations</em> — true, and useless, because a
/// registration nothing resolves never runs. These assert the composed chain instead, which is
/// what decides whether the feature is on.
/// </para>
/// </remarks>
public sealed class PipelinePlacementTests
{
    [Fact]
    public void UseRaptor_WithNoPipelineDelegates_PlacesIngestionBehaviourAfterEmbedding()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseRaptor());

        var types = IngestionChain(services);

        Assert.Contains(typeof(RaptorIngestionBehavior), types);
        Assert.Equal(
            types.IndexOf(typeof(EmbeddingBehavior)) + 1,
            types.IndexOf(typeof(RaptorIngestionBehavior)));
    }

    [Fact]
    public void UseRaptor_WithNoPipelineDelegates_PlacesRetrievalBehaviourBeforeReranking()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseRaptor());

        var types = RetrievalChain(services);

        Assert.Contains(typeof(RaptorRetrievalBehavior), types);
        Assert.Equal(
            types.IndexOf(typeof(RerankingBehavior)) - 1,
            types.IndexOf(typeof(RaptorRetrievalBehavior)));
    }

    /// <summary>
    /// The three-delegate form <c>docs/guide/raptor.md</c> teaches keeps working, and keeps
    /// placing each behaviour exactly once, at the position the caller asked for.
    /// </summary>
    /// <remarks>
    /// The delegates run before <c>configure</c> does, so the caller's placement lands first and
    /// <c>UseRaptor</c>'s own <c>Add</c> is the idempotent no-op. Inserting twice would run the
    /// same singleton at two points in the chain — a second tree build per document — with
    /// nothing about the container looking wrong.
    /// </remarks>
    [Fact]
    public void UseRaptor_WithTheDocumentedDelegates_StillPlacesEachBehaviourExactlyOnce()
    {
        var services = new ServiceCollection();
        services.AddRagNet(
            configure: rag => rag.UseRaptor(),
            ingestion: pipeline => pipeline
                .Add<RaptorIngestionBehavior>(after: typeof(SparseEmbeddingBehavior)),
            retrieval: pipeline => pipeline
                .Add<RaptorRetrievalBehavior>(before: typeof(VectorStoreBehavior)));

        var ingestion = IngestionChain(services);
        var retrieval = RetrievalChain(services);

        Assert.Equal(1, ingestion.Count(t => t == typeof(RaptorIngestionBehavior)));
        Assert.Equal(1, retrieval.Count(t => t == typeof(RaptorRetrievalBehavior)));

        // The caller's positions win, not the defaults this fix added.
        Assert.Equal(
            ingestion.IndexOf(typeof(SparseEmbeddingBehavior)) + 1,
            ingestion.IndexOf(typeof(RaptorIngestionBehavior)));
        Assert.Equal(
            retrieval.IndexOf(typeof(VectorStoreBehavior)) - 1,
            retrieval.IndexOf(typeof(RaptorRetrievalBehavior)));
    }

    /// <summary>
    /// Called on a <see cref="RagBuilder"/> that never went through <c>AddRagNet</c>, there is no
    /// pipeline to place anything in — so it says so rather than registering into the void.
    /// </summary>
    [Fact]
    public void UseRaptor_WithoutAddRagNet_ThrowsNamingWhatIsMissing()
    {
        var builder = new RagBuilder(new ServiceCollection());

        var ex = Assert.Throws<InvalidOperationException>(() => builder.UseRaptor());

        Assert.Contains("UseRaptor", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddRagNet", ex.Message, StringComparison.Ordinal);
    }

    private static List<Type> IngestionChain(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return [.. provider.GetRequiredService<IngestionPipelineBuilder>().GetBehaviorTypes()];
    }

    private static List<Type> RetrievalChain(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return [.. provider.GetRequiredService<RetrievalPipelineBuilder>().GetBehaviorTypes()];
    }
}
