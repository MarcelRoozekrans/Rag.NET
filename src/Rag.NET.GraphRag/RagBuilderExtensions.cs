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
    /// <exception cref="ArgumentException">
    /// The configured <see cref="GraphRagOptions"/> or <see cref="GraphRagRetrievalOptions"/>
    /// violate a documented constraint — the generated validators reject the registration at
    /// the configuring line rather than letting a bad value crash ingestion, hang global
    /// search, or silently corrupt the PageRank blend.
    /// </exception>
    public static RagBuilder UseGraphRag(
        this RagBuilder builder,
        Action<GraphRagOptions>? configure = null,
        Action<GraphRagRetrievalOptions>? retrieval = null,
        Action<GraphStoreBuilder>? graph = null)
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
