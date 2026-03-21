using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
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
        services.AddSingleton<InMemoryBm25Index>();

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
                var chatEngine = new ChatAnswerEngine(chatClient);
                var mapReduceEngine = new MapReduceAnswerEngine(chatClient,
                    sp.GetRequiredService<ILogger<MapReduceAnswerEngine>>());
                var refineEngine = new RefineAnswerEngine(chatClient,
                    sp.GetRequiredService<ILogger<RefineAnswerEngine>>());
                answerEngine = new DispatchingAnswerEngine(chatEngine, mapReduceEngine, refineEngine);
            }
            return new RagPipeline(r, i, answerEngine);
        });

        var builder = new RagBuilder(services);
        configure?.Invoke(builder);

        // Default fallback — no-op when UseSqlitePersistence() has already registered IBm25Index.
        services.TryAddSingleton<IBm25Index>(sp => sp.GetRequiredService<InMemoryBm25Index>());

        return services;
    }
}
