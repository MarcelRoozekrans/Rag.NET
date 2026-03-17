using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Ingestion;
using Rag.NET.Models.Options;
using Rag.NET.MultiQuery;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Rag.NET.Search;
using Rag.NET.Storage;

namespace Rag.NET.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNet(
        this IServiceCollection services,
        Action<RagBuilder>? configure = null)
    {
        // ZeroAlloc.Inject-generated: registers IDocumentParser (Text, Markdown),
        // IChunkingStrategy (Recursive). IIngestor is registered manually below
        // to support optional ParentDocumentOptions/InMemoryParentChunkStore parameters.
        services.AddRagNETServices();

        services.TryAddSingleton<ChunkingOptions>();
        services.AddSingleton<InMemoryBm25Index>();

        services.AddSingleton<IIngestor>(sp => new DocumentIngestor(
            sp.GetServices<IDocumentParser>(),
            sp.GetRequiredService<IChunkingStrategy>(),
            sp.GetRequiredService<IVectorStore>(),
            sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            sp.GetRequiredService<ChunkingOptions>(),
            sp.GetRequiredService<InMemoryBm25Index>(),
            sp.GetService<InMemoryParentChunkStore>(),
            sp.GetService<ParentDocumentOptions>()));

        services.AddSingleton<IRetriever>(sp => BuildRetrieverChain(sp));

        services.AddSingleton<IRagPipeline>(sp =>
        {
            var retriever = sp.GetRequiredService<IRetriever>();
            var ingestor = sp.GetRequiredService<IIngestor>();

            var chatClient = sp.GetService<IChatClient>();
            IAnswerEngine? answerEngine = chatClient is not null ? new ChatAnswerEngine(chatClient) : null;

            return new RagPipeline(retriever, ingestor, answerEngine);
        });

        var builder = new RagBuilder(services);
        configure?.Invoke(builder);

        return services;
    }

    private static IRetriever BuildRetrieverChain(IServiceProvider sp)
    {
        var store = sp.GetRequiredService<IVectorStore>();
        var embedder = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var bm25Index = sp.GetRequiredService<InMemoryBm25Index>();

        IRetriever chain = new VectorStoreRetriever(store, embedder, bm25Index);
        chain = WrapWithQueryDecorators(chain, sp);

        var parentDocOptions = sp.GetService<ParentDocumentOptions>();
        var parentStore = sp.GetService<InMemoryParentChunkStore>();
        if (parentDocOptions is not null && parentStore is not null)
        {
            chain = new ParentDocumentRetriever(
                chain,
                parentStore,
                sp.GetService<ILogger<ParentDocumentRetriever>>());
        }

        chain = new RedundancyFilterRetriever(chain, embedder, sp.GetService<ILogger<RedundancyFilterRetriever>>());

        if (sp.GetService<MmrEnabled>() is not null)
        {
            chain = new MmrRetriever(chain, embedder, sp.GetService<ILogger<MmrRetriever>>());
        }

        chain = new LostInTheMiddleRetriever(chain);

        var cachingOptions = sp.GetService<CachingOptions>();
        var hybridCache = sp.GetService<HybridCache>();
        if (cachingOptions is not null && hybridCache is not null)
        {
            chain = new ResultCacheRetriever(
                chain,
                hybridCache,
                cachingOptions,
                sp.GetService<ILogger<ResultCacheRetriever>>());
        }

        return chain;
    }

    private static IRetriever WrapWithQueryDecorators(IRetriever chain, IServiceProvider sp)
    {
        var cachingOptions = sp.GetService<CachingOptions>();
        var hybridCache = sp.GetService<HybridCache>();

        if (cachingOptions is not null && hybridCache is not null)
        {
            chain = new EmbeddingCacheRetriever(
                chain,
                hybridCache,
                cachingOptions,
                sp.GetService<ILogger<EmbeddingCacheRetriever>>());
        }

        var hydeGenerator = sp.GetService<IHypotheticalDocumentGenerator>();
        if (hydeGenerator is not null)
        {
            chain = new HydeRetriever(
                chain,
                hydeGenerator,
                sp.GetService<ILogger<HydeRetriever>>());
        }

        var queryExpander = sp.GetService<IQueryExpander>();
        if (queryExpander is not null)
        {
            chain = new MultiQueryRetriever(
                chain,
                queryExpander,
                sp.GetService<MultiQueryOptions>() ?? new MultiQueryOptions(),
                sp.GetService<ILogger<MultiQueryRetriever>>());
        }

        var reranker = sp.GetService<IReranker>();
        if (reranker is not null)
        {
            chain = new RerankingRetriever(
                chain,
                reranker,
                sp.GetService<ILogger<RerankingRetriever>>());
        }

        return chain;
    }
}
