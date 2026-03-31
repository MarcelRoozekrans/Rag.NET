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
            IAnswerEngine? answerEngine = sp.GetService<IAnswerEngine>();
            if (answerEngine is null && chatClient is not null)
            {
                var conversationMemory = sp.GetService<IConversationMemory>();
                answerEngine = new ChatAnswerEngine(chatClient, conversationMemory);
            }
            return new RagPipeline(r, i, answerEngine);
        });

        var builder = new RagBuilder(services);
        configure?.Invoke(builder);
        WireRefinementStrategy(services);
        WireDeepResearch(services);
        WireTimeWeighting(services);
        WireTagRetrieval(services);

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
        // never used. This is a known limitation of decorating ZeroAlloc-generated
        // registrations; PipelineRetriever holds only a Pipeline<> reference so the
        // extra instance carries no cost beyond memory.
        services.AddSingleton<PipelineRetriever>(sp => new PipelineRetriever
        {
            Pipeline = sp.GetRequiredService<Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>>(),
            Logger   = sp.GetService<ILogger<PipelineRetriever>>(),
        });

        // Register as concrete type so WireTagRetrieval can resolve it for stacking
        services.AddSingleton<DeepResearchRetriever>(sp => new DeepResearchRetriever(
            sp.GetRequiredService<PipelineRetriever>(),
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<DeepResearchOptions>(),
            sp.GetService<ILogger<DeepResearchRetriever>>()));

        // Replace IRetriever with the decorator (superseded by WireTagRetrieval if both are used)
        services.AddSingleton<IRetriever>(sp => sp.GetRequiredService<DeepResearchRetriever>());
    }

    private static void WireTimeWeighting(IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeWeightedOptions)))
            return;

        // DeepResearchRetriever descriptor is registered by WireDeepResearch (called above in AddRagNet).
        // Ordering is load-bearing: WireDeepResearch must run before WireTimeWeighting.
        bool hasDeepResearch = services.Any(d => d.ServiceType == typeof(DeepResearchRetriever));

        // When DeepResearch is not wired, PipelineRetriever may not be registered as its own
        // concrete type. Register it here so TimeWeightedRetriever can wrap it — same pattern
        // as WireDeepResearch.
        if (!hasDeepResearch)
        {
            services.TryAddSingleton<PipelineRetriever>(sp => new PipelineRetriever
            {
                Pipeline = sp.GetRequiredService<Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>>(),
                Logger   = sp.GetService<ILogger<PipelineRetriever>>(),
            });
        }

        services.AddSingleton<TimeWeightedRetriever>(sp =>
        {
            IRetriever inner = hasDeepResearch
                ? sp.GetRequiredService<DeepResearchRetriever>()
                : (IRetriever)sp.GetRequiredService<PipelineRetriever>();

            return new TimeWeightedRetriever(
                inner,
                sp.GetRequiredService<TimeWeightedOptions>(),
                sp.GetService<ILogger<TimeWeightedRetriever>>());
        });

        services.AddSingleton<IRetriever>(sp => sp.GetRequiredService<TimeWeightedRetriever>());
    }

    private static void WireTagRetrieval(IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TagRetrievalOptions)))
            return;

        // DeepResearchRetriever and TimeWeightedRetriever descriptors are registered by their
        // respective Wire* methods (called above in AddRagNet).
        // Ordering is load-bearing: WireDeepResearch and WireTimeWeighting must run before WireTagRetrieval.
        bool hasDeepResearch = services.Any(d => d.ServiceType == typeof(DeepResearchRetriever));
        bool hasTimeWeighted = services.Any(d => d.ServiceType == typeof(TimeWeightedRetriever));

        // When neither DeepResearch nor TimeWeighted is wired, PipelineRetriever was never
        // registered as its concrete type (ZeroAlloc registers it only as IRetriever).
        // Register it here so TagRetriever can wrap it — same pattern as WireDeepResearch.
        if (!hasDeepResearch && !hasTimeWeighted)
        {
            services.TryAddSingleton<PipelineRetriever>(sp => new PipelineRetriever
            {
                Pipeline = sp.GetRequiredService<Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>>(),
                Logger   = sp.GetService<ILogger<PipelineRetriever>>(),
            });
        }

        // Stacking order (outermost first):
        // TagRetriever → TimeWeightedRetriever → DeepResearchRetriever → PipelineRetriever
        services.AddSingleton<TagRetriever>(sp =>
        {
            IRetriever inner;
            if (hasTimeWeighted)
                inner = sp.GetRequiredService<TimeWeightedRetriever>();
            else if (hasDeepResearch)
                inner = sp.GetRequiredService<DeepResearchRetriever>();
            else
                inner = sp.GetRequiredService<PipelineRetriever>();

            return new TagRetriever(
                inner,
                sp.GetRequiredService<ITagIndex>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                sp.GetRequiredService<TagRetrievalOptions>(),
                sp.GetService<ILogger<TagRetriever>>());
        });

        services.AddSingleton<IRetriever>(sp => sp.GetRequiredService<TagRetriever>());
    }
}
