using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Graph;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Retrieval.Behaviors;

namespace Rag.NET.GraphRag;

/// <summary>Extension methods for registering GraphRAG in the Rag.NET pipeline.</summary>
public static class RagBuilderExtensions
{
    /// <summary>
    /// Enables GraphRAG — entity extraction, community detection, and graph-aware retrieval.
    /// Places <see cref="GraphEntityExtractionBehavior"/> and, after it,
    /// <see cref="CommunityDetectionBehavior"/> into ingestion following <c>EmbeddingBehavior</c>,
    /// and <see cref="GraphLocalSearchBehavior"/> into retrieval before <c>RerankingBehavior</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placing them is the whole point of the call.</b> This method used to register four
    /// behaviours and stop, and none of those types is in either default pipeline, so
    /// <c>UseGraphRag()</c> on its own extracted no entities, detected no communities and built no
    /// graph — while retrieval quietly stayed a plain vector search (issue #191).
    /// </para>
    /// <para>
    /// <b><see cref="GraphGlobalSearchBehavior"/> is deliberately not placed.</b> Which search
    /// runs is the caller's decision, as <c>docs/guide/graphrag.md</c> states: local search is a
    /// graph traversal over results retrieval already produced, while global search re-enters the
    /// pipeline for community reports and runs an LLM map-reduce over them on every query.
    /// Enabling that by default would be per-query spend nobody asked for. It stays registered, so
    /// naming it in <c>AddRagNet</c>'s <c>retrieval:</c> delegate is all it takes.
    /// </para>
    /// <para>
    /// The explicit form the guide teaches is unchanged and still wins: those delegates run before
    /// <c>configure</c>, and <c>Add</c> is idempotent, so a caller who places these by hand keeps
    /// their own positions and gets each behaviour exactly once.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The configured <see cref="GraphRagOptions"/> or <see cref="GraphRagRetrievalOptions"/>
    /// violate a documented constraint — the generated validators reject the registration at
    /// the configuring line rather than letting a bad value crash ingestion, hang global
    /// search, or silently corrupt the PageRank blend.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <c>AddRagNet</c> has not been called, so there is no pipeline to place the behaviours in.
    /// Registering them anyway is what made the silent no-op possible.
    /// </exception>
    public static TBuilder UseGraphRag<TBuilder>(
        this TBuilder builder,
        Action<GraphRagOptions>? configure = null,
        Action<GraphRagRetrievalOptions>? retrieval = null,
        Action<GraphStoreBuilder>? graph = null)
        where TBuilder : IRagBuilder
    {
        var options = new GraphRagOptions();
        configure?.Invoke(options);
        ThrowIfInvalid(new GraphRagOptionsValidator().Validate(options), nameof(configure), "GraphRAG ingestion");
        builder.Services.AddSingleton(options);

        var retrievalOptions = new GraphRagRetrievalOptions();
        retrieval?.Invoke(retrievalOptions);
        ThrowIfInvalid(new GraphRagRetrievalOptionsValidator().Validate(retrievalOptions), nameof(retrieval), "GraphRAG retrieval");
        builder.Services.AddSingleton(retrievalOptions);

        // Graph store — default to in-memory SQLite if not configured
        var graphStoreBuilder = new GraphStoreBuilder(builder.Services);
        if (graph is not null)
            graph(graphStoreBuilder);
        else
            graphStoreBuilder.UseSqlite(":memory:");

        // Ingestion behaviors
        builder.Services.AddSingleton<GraphEntityExtractionBehavior>(sp =>
            new GraphEntityExtractionBehavior(
                options.ExtractionChatClient ?? sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                sp.GetRequiredService<IGraphStore>(),
                options));

        builder.Services.AddSingleton<CommunityDetectionBehavior>(sp =>
            new CommunityDetectionBehavior(
                options.SummarizationChatClient ?? sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                sp.GetRequiredService<IGraphStore>(),
                options));

        // The on-demand counterpart to the ingest-time growth threshold (#300). Registered rather
        // than left for callers to construct, because it must share the ONE CommunityDetectionBehavior
        // singleton: that instance holds the debounce baseline, and a rebuild resets it so ingestion
        // continuing afterwards debounces from the rebuilt state rather than a stale count.
        builder.Services.AddSingleton<GraphProjectionRebuilder>(sp =>
            new GraphProjectionRebuilder(
                sp.GetRequiredService<CommunityDetectionBehavior>(),
                sp.GetRequiredService<IVectorStore>()));

        // Retrieval behaviors
        builder.Services.AddSingleton<GraphLocalSearchBehavior>(sp =>
            new GraphLocalSearchBehavior(
                sp.GetRequiredService<IGraphStore>(),
                retrievalOptions));

        builder.Services.AddSingleton<GraphGlobalSearchBehavior>(sp =>
            new GraphGlobalSearchBehavior(
                retrievalOptions.GlobalChatClient ?? sp.GetRequiredService<IChatClient>(),
                retrievalOptions));

        builder.Services.AddSingleton<GraphChunkFilterBehavior>(sp =>
            new GraphChunkFilterBehavior(retrievalOptions));

        PlaceGraphRagBehaviors(builder.Services);

        return builder;
    }

    /// <summary>
    /// Puts the three behaviours a bare <c>UseGraphRag()</c> should run into the two pipelines,
    /// at the positions <c>docs/guide/graphrag.md</c>'s quick start uses.
    /// </summary>
    /// <param name="services">The collection <c>AddRagNet</c> was called on.</param>
    /// <exception cref="InvalidOperationException">There is no pipeline to place them in.</exception>
    private static void PlaceGraphRagBehaviors(IServiceCollection services)
    {
        // Extraction needs the chunk embeddings, and detection needs the graph extraction wrote.
        services.RagIngestionPipeline(nameof(UseGraphRag))
            .Add<GraphEntityExtractionBehavior>(after: typeof(EmbeddingBehavior))
            .Add<CommunityDetectionBehavior>(after: typeof(GraphEntityExtractionBehavior));

        // Local search only; GraphGlobalSearchBehavior is opt-in — see this method's caller.
        //
        // The filter is placed BEFORE local search, and the order is the whole point (#247). Running
        // earlier means it WRAPS the graph behaviours: they still receive every entity, relationship
        // and report chunk and can traverse, blend and summarise with them, while only what reaches
        // the caller is filtered. Placed inside, it would starve the behaviours it exists to make
        // usable.
        //
        // Local search is added FIRST so the filter's `before:` anchor exists to resolve against.
        // Added the other way round, the anchor is not yet in the pipeline, `before:` silently
        // degrades to an append, and the filter lands at the END of the chain — inside local search
        // rather than outside it. That is precisely the misplacement
        // UseGraphRag_PlacesTheChunkFilterOutsideTheGraphSearchBehaviours exists to catch, and it
        // caught it here.
        services.RagRetrievalPipeline(nameof(UseGraphRag))
            .Add<GraphLocalSearchBehavior>(before: typeof(RerankingBehavior))
            .Add<GraphChunkFilterBehavior>(before: typeof(GraphLocalSearchBehavior));
    }

    /// <summary>
    /// Enables mind-map extraction — builds a hierarchical concept tree from document content
    /// via a single LLM call. Nodes are stored in IGraphStore (if registered) as GraphEntity
    /// with Type = "mind_map_node". Places <see cref="MindMapExtractionBehavior"/> into ingestion
    /// directly after <c>ChunkSanitiserBehavior</c>, so extraction reads the same sanitised text
    /// that gets embedded and stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This was the worst of the three silent no-ops in issue #191.</b> The behaviour was
    /// registered and in no pipeline, exactly as with <c>UseRaptor</c> and <c>UseGraphRag</c> —
    /// but mind-map extraction has no guide page, so unlike those two there was nowhere at all a
    /// caller could have learned that the call needed an <c>ingestion:</c> delegate naming
    /// <see cref="MindMapExtractionBehavior"/> before it did anything.
    /// </para>
    /// <para>
    /// <see cref="MindMapOptions.ExtractAtIngestion"/> remains the on-switch and still defaults to
    /// <see langword="false"/>: the placed behaviour passes the document straight through until it
    /// is set. That default is deliberate — <see cref="MindMapExtractor"/> is registered for
    /// callers who want to extract on demand rather than on every ingest — but it is now the
    /// <em>only</em> thing standing between the call and a working extraction, and it is a
    /// documented property rather than an undocumented second registration step.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <c>AddRagNet</c> has not been called, so there is no pipeline to place the behaviour in.
    /// </exception>
    public static TBuilder UseMindMapExtraction<TBuilder>(
        this TBuilder builder,
        Action<MindMapOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var options = new MindMapOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        builder.Services.AddSingleton<MindMapExtractor>(sp =>
            new MindMapExtractor(
                options.ChatClient ?? sp.GetRequiredService<IChatClient>(),
                sp.GetService<IGraphStore>(),
                options,
                sp.GetService<ILogger<MindMapExtractor>>()));

        builder.Services.AddSingleton<MindMapExtractionBehavior>(sp =>
            new MindMapExtractionBehavior(
                sp.GetRequiredService<MindMapExtractor>(),
                options));

        builder.Services.RagIngestionPipeline(nameof(UseMindMapExtraction))
            .Add<MindMapExtractionBehavior>(after: typeof(ChunkSanitiserBehavior));

        return builder;
    }

    /// <summary>
    /// Rejects invalid options at the line that configured them, with a stack trace pointing at
    /// the caller's lambda rather than at some later ingestion or retrieval that happens to
    /// consume the singleton — the same registration-time shape as
    /// <c>RagBuilder.UseChunkingStrategy</c> (issue #90).
    /// </summary>
    /// <param name="result">The generated validator's verdict on the configured options.</param>
    /// <param name="paramName">The caller's configuring delegate, for <see cref="ArgumentException.ParamName"/>.</param>
    /// <param name="description">What was being configured, for the failure message.</param>
    /// <exception cref="ArgumentException">The options violate a declared constraint.</exception>
    private static void ThrowIfInvalid(
        ZeroAlloc.Validation.ValidationResult result, string paramName, string description)
    {
        if (result.IsValid)
        {
            return;
        }

        // Projected by index into an array: ValidationFailure is a non-readonly struct, so
        // enumerating the span by value trips EPS06 and indexing the property result directly
        // trips HLQ013 — same shape as RagBuilder.ThrowIfInvalid.
        var failures = result.Failures;
        var described = new string[failures.Length];
        for (var i = 0; i < failures.Length; i++)
        {
            described[i] = $"{failures[i].PropertyName} — {failures[i].ErrorMessage}";
        }

        throw new ArgumentException(
            $"The {description} options configured here are invalid: " +
            string.Join("; ", described),
            paramName);
    }
}
