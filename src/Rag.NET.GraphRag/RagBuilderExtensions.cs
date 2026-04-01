using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.DependencyInjection;
using Rag.NET.Graph;

namespace Rag.NET.GraphRag;

/// <summary>Extension methods for registering GraphRAG in the Rag.NET pipeline.</summary>
public static class RagBuilderExtensions
{
    /// <summary>
    /// Enables GraphRAG — entity extraction, community detection, and graph-aware retrieval.
    /// </summary>
    public static RagBuilder UseGraphRag(
        this RagBuilder builder,
        Action<GraphRagOptions>? configure = null,
        Action<GraphRagRetrievalOptions>? retrieval = null,
        Action<GraphStoreBuilder>? graph = null)
    {
        var options = new GraphRagOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        var retrievalOptions = new GraphRagRetrievalOptions();
        retrieval?.Invoke(retrievalOptions);
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

        // Retrieval behaviors
        builder.Services.AddSingleton<GraphLocalSearchBehavior>(sp =>
            new GraphLocalSearchBehavior(
                sp.GetRequiredService<IGraphStore>(),
                retrievalOptions));

        builder.Services.AddSingleton<GraphGlobalSearchBehavior>(sp =>
            new GraphGlobalSearchBehavior(
                retrievalOptions.GlobalChatClient ?? sp.GetRequiredService<IChatClient>(),
                retrievalOptions));

        return builder;
    }

    /// <summary>
    /// Enables mind-map extraction — builds a hierarchical concept tree from document content
    /// via a single LLM call. Nodes are stored in IGraphStore (if registered) as GraphEntity
    /// with Type = "mind_map_node".
    /// </summary>
    public static RagBuilder UseMindMapExtraction(
        this RagBuilder builder,
        Action<MindMapOptions>? configure = null)
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

        return builder;
    }
}
