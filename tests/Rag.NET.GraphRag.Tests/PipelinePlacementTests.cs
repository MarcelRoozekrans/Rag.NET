using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Rag.NET.GraphRag.LocalSearch;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// <c>UseGraphRag()</c> and <c>UseMindMapExtraction()</c> on their own actually reach a pipeline
/// (issue #191).
/// </summary>
/// <remarks>
/// <para>
/// Both used to register their behaviours as singletons and stop there. None of those types is in
/// either default pipeline, and <c>Build</c> only ever resolves the types the pipeline lists, so
/// the registrations were unreachable: entity extraction never ran, community detection never
/// ran, no graph was ever built, and retrieval quietly stayed a plain vector search.
/// </para>
/// <para>
/// <c>UseMindMapExtraction</c> was the worst of the three — it has no guide page, so there was
/// nowhere a user could have learned that the call needed <c>ingestion:</c> to do anything at all.
/// </para>
/// </remarks>
public sealed class PipelinePlacementTests
{
    [Fact]
    public void UseGraphRag_WithNoPipelineDelegates_PlacesBothIngestionBehavioursAfterEmbedding()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseGraphRag());

        var types = IngestionChain(services);

        Assert.Equal(
            types.IndexOf(typeof(EmbeddingBehavior)) + 1,
            types.IndexOf(typeof(GraphEntityExtractionBehavior)));
        Assert.Equal(
            types.IndexOf(typeof(GraphEntityExtractionBehavior)) + 1,
            types.IndexOf(typeof(CommunityDetectionBehavior)));
    }

    /// <remarks>
    /// <para>
    /// The router has to sit after BOTH graph behaviours and before storage (#247). After entity
    /// extraction alone it would miss the community reports detection adds; before extraction it
    /// would find nothing at all. Either mistake separates some of the graph's chunks and leaves the
    /// rest in the document store — a partial separation, which reads as a working one until a
    /// measurement says otherwise.
    /// </para>
    /// <para>
    /// Asserted on the built chain because <c>after:</c> silently degrades to an append when its
    /// anchor is not yet in the pipeline, so the registration reading correctly proves nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void UseGraphRag_PlacesTheChunkRouterAfterBothGraphBehaviours()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseGraphRag());

        var types = IngestionChain(services);
        var router = types.IndexOf(typeof(GraphChunkRoutingBehavior));
        var extraction = types.IndexOf(typeof(GraphEntityExtractionBehavior));
        var detection = types.IndexOf(typeof(CommunityDetectionBehavior));

        Assert.True(router >= 0, "GraphChunkRoutingBehavior is not in the ingestion chain at all.");
        Assert.True(
            router > extraction && router > detection,
            $"The router is at {router}, extraction at {extraction}, detection at {detection}. " +
            "It must run after both or it separates only some of the graph's chunks.");
    }

    /// <summary>
    /// <c>UseGraphRag()</c> registers the local search it ships — <see cref="IGraphRagSearch"/> —
    /// as a service, even though it places no retrieval-pipeline behaviour for it.
    /// </summary>
    /// <remarks>
    /// This is the only registration guard for <see cref="IGraphRagSearch"/> in the repository.
    /// It is not in either pipeline chain (see
    /// <see cref="UseGraphRag_WithNoPipelineDelegates_LeavesGlobalSearchOutOfTheChain"/> and its
    /// ingestion counterparts) and it is not resolved by any other test, so nothing else would
    /// catch a change that broke resolving it — a caller following
    /// <c>docs/guide/graphrag.md</c>'s instruction to call <c>IGraphRagSearch</c> directly would
    /// be the first to find out.
    /// </remarks>
    [Fact]
    public void UseGraphRag_WithNoPipelineDelegates_RegistersLocalSearchAsAService()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseGraphRag());

        Assert.Contains(services, d => d.ServiceType == typeof(IGraphRagSearch));
    }

    /// <summary>
    /// Global search stays opt-in, because which search runs is the caller's decision.
    /// </summary>
    /// <remarks>
    /// <c>docs/guide/graphrag.md</c> states it outright — "which search runs is decided by the
    /// behaviors you register, not by a setting" — and the cost is why. Local search is a graph
    /// traversal over whatever the retrieval underneath already returned;
    /// <see cref="GraphGlobalSearchBehavior"/> re-enters the pipeline to fetch community reports
    /// and then runs an LLM map-reduce over them, on <em>every</em> query. Auto-placing it would
    /// turn a bare <c>UseGraphRag()</c> into per-query LLM spend the caller never asked for, which
    /// is the opposite failure to the one this fix is closing but no more welcome.
    /// </remarks>
    [Fact]
    public void UseGraphRag_WithNoPipelineDelegates_LeavesGlobalSearchOutOfTheChain()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseGraphRag());

        Assert.DoesNotContain(typeof(GraphGlobalSearchBehavior), RetrievalChain(services));

        // Still registered, so the caller who wants it can place it by name.
        Assert.Contains(services, d => d.ServiceType == typeof(GraphGlobalSearchBehavior));
    }

    [Fact]
    public void UseGraphRag_WithGlobalSearchPlacedByHand_HonoursTheCallersChoice()
    {
        var services = new ServiceCollection();
        services.AddRagNet(
            configure: rag => rag.UseGraphRag(),
            retrieval: pipeline => pipeline
                .Add<GraphGlobalSearchBehavior>(before: typeof(RerankingBehavior)));

        var types = RetrievalChain(services);

        Assert.Contains(typeof(GraphGlobalSearchBehavior), types);
    }

    [Fact]
    public void UseMindMapExtraction_WithNoPipelineDelegates_PlacesItsBehaviourInTheChain()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseMindMapExtraction(o => o.ExtractAtIngestion = true));

        var types = IngestionChain(services);

        Assert.Contains(typeof(MindMapExtractionBehavior), types);
        Assert.Equal(
            types.IndexOf(typeof(ChunkSanitiserBehavior)) + 1,
            types.IndexOf(typeof(MindMapExtractionBehavior)));
    }

    [Fact]
    public void UseGraphRag_WithoutAddRagNet_ThrowsNamingWhatIsMissing()
    {
        var builder = new RagBuilder(new ServiceCollection());

        var ex = Assert.Throws<InvalidOperationException>(() => builder.UseGraphRag());

        Assert.Contains("UseGraphRag", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddRagNet", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseMindMapExtraction_WithoutAddRagNet_ThrowsNamingWhatIsMissing()
    {
        var builder = new RagBuilder(new ServiceCollection());

        var ex = Assert.Throws<InvalidOperationException>(() => builder.UseMindMapExtraction());

        Assert.Contains("UseMindMapExtraction", ex.Message, StringComparison.Ordinal);
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
