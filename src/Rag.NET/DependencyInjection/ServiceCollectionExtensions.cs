using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Rag.NET.Search;

namespace Rag.NET.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNet(
        this IServiceCollection services,
        Action<RagBuilder>? configure = null,
        Action<IngestionPipelineBuilder>? ingestion = null,
        Action<RetrievalPipelineBuilder>? retrieval = null)
    {
        // ZeroAlloc.Inject-generated: registers IDocumentParser (Text, Markdown),
        // IChunkingStrategy (Recursive), all [Singleton] behaviors,
        // PipelineIngestor (as IIngestor), PipelineRetriever (as IRetriever).
        services.AddRagNETServices();

        services.TryAddSingleton<ChunkingOptions>();
        services.AddSingleton<InMemoryBm25Index>(sp => new InMemoryBm25Index(sp.GetService<SynonymMap>()));

        // Build and register pipelines — behaviors are resolved from the container by builders
        var ingestionBuilder = new IngestionPipelineBuilder();
        ingestion?.Invoke(ingestionBuilder);
        services.AddSingleton(sp => ingestionBuilder.Build(sp));

        var retrievalBuilder = new RetrievalPipelineBuilder();
        retrieval?.Invoke(retrievalBuilder);
        services.AddSingleton(sp => retrievalBuilder.Build(sp));

        services.AddSingleton<IRagPipeline>(sp =>
        {
            var r = sp.GetRequiredService<IRetriever>();
            var i = sp.GetRequiredService<IIngestor>();
            var chatClient = sp.GetService<IChatClient>();
            IAnswerEngine? answerEngine = null;
            if (chatClient is not null)
            {
                var conversationMemory = sp.GetService<IConversationMemory>();
                var chatEngine = new ChatAnswerEngine(chatClient, conversationMemory);
                var mapReduceEngine = new MapReduceAnswerEngine(chatClient,
                    sp.GetRequiredService<ILogger<MapReduceAnswerEngine>>(), conversationMemory);
                var refineEngine = new RefineAnswerEngine(chatClient,
                    sp.GetRequiredService<ILogger<RefineAnswerEngine>>(), conversationMemory);
                answerEngine = new DispatchingAnswerEngine(chatEngine, mapReduceEngine, refineEngine);
            }
            return new RagPipeline(r, i, answerEngine);
        });

        var builder = new RagBuilder(services);
        configure?.Invoke(builder);
        WireRefinementStrategy(services);
        WireDeepResearch(services);

        // Default fallback — no-op when UseSqlitePersistence() has already registered IBm25Index.
        services.TryAddSingleton<IBm25Index>(sp => sp.GetRequiredService<InMemoryBm25Index>());

        return services;
    }

    /// <summary>
    /// Replaces the ZeroAlloc-generated <see cref="ParseBehavior"/> singleton registration with a
    /// factory that wires <see cref="ParseBehavior.RefinementStrategy"/> from DI when an
    /// <see cref="IChunkRefinementStrategy"/> is registered.
    /// <see cref="ParseBehavior.RefinementStrategy"/> cannot use <c>[Inject]</c> because
    /// ZeroAlloc.Inject calls <c>GetRequiredService</c> for all injected properties, which
    /// would throw when no refinement strategy is configured.
    /// </summary>
    private static void WireRefinementStrategy(IServiceCollection services) =>
        services.AddSingleton<ParseBehavior>(sp => new ParseBehavior
        {
            Parsers = sp.GetRequiredService<IEnumerable<IDocumentParser>>(),
            ChunkingStrategy = sp.GetRequiredService<IChunkingStrategy>(),
            ChunkingOptions = sp.GetRequiredService<ChunkingOptions>(),
            RefinementStrategy = sp.GetService<IChunkRefinementStrategy>(),
        });

    private static void WireDeepResearch(IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(DeepResearchOptions)))
            return;

        // PipelineRetriever is registered only as IRetriever by ZeroAlloc ([Singleton(As = typeof(IRetriever))]).
        // Register it by its concrete type with manually-wired [Inject] properties so the decorator can wrap it.
        // NOTE: This registers a second PipelineRetriever instance separate from the one
        // ZeroAlloc registered as IRetriever. The generated IRetriever→PipelineRetriever
        // registration is superseded by the decorator below, so the orphaned instance is
        // never used — but two singletons of PipelineRetriever will exist in the container.
        services.AddSingleton<PipelineRetriever>(sp => new PipelineRetriever
        {
            Pipeline = sp.GetRequiredService<Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>>(),
            Logger   = sp.GetService<ILogger<PipelineRetriever>>(),
        });

        // Replace IRetriever with the decorator (last AddSingleton<IRetriever> wins).
        services.AddSingleton<IRetriever>(sp => new DeepResearchRetriever(
            sp.GetRequiredService<PipelineRetriever>(),
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<DeepResearchOptions>(),
            sp.GetService<ILogger<DeepResearchRetriever>>()));
    }
}
