using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Chunking;
using Rag.NET.Ingestion;
using Rag.NET.Models.Options;
using Rag.NET.MultiQuery;
using Rag.NET.Parsers;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Rag.NET.Search;

namespace Rag.NET.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagNet(
        this IServiceCollection services,
        Action<RagBuilder>? configure = null)
    {
        services.AddSingleton<IDocumentParser, TextDocumentParser>();
        services.AddSingleton<IDocumentParser, MarkdownDocumentParser>();

        services.TryAddSingleton<ChunkingOptions>();
        services.TryAddSingleton<IChunkingStrategy, RecursiveChunkingStrategy>();
        services.AddSingleton<InMemoryBm25Index>();

        services.TryAddSingleton<IRetriever>(sp =>
        {
            var store = sp.GetRequiredService<IVectorStore>();
            var embedder = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            var bm25Index = sp.GetRequiredService<InMemoryBm25Index>();
            return new VectorStoreRetriever(store, embedder, bm25Index);
        });

        services.TryAddSingleton<IIngestor>(sp =>
        {
            var parsers = sp.GetServices<IDocumentParser>();
            var chunker = sp.GetRequiredService<IChunkingStrategy>();
            var store = sp.GetRequiredService<IVectorStore>();
            var embedder = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            var options = sp.GetRequiredService<ChunkingOptions>();
            var bm25Index = sp.GetRequiredService<InMemoryBm25Index>();
            return new DocumentIngestor(parsers, chunker, store, embedder, options, bm25Index);
        });

        services.TryAddSingleton<IAnswerEngine>(sp =>
        {
            var chatClient = sp.GetService<IChatClient>();
            if (chatClient is null) return null!;
            return new ChatAnswerEngine(chatClient);
        });

        services.AddSingleton<IRagPipeline>(sp =>
        {
            var retriever = sp.GetRequiredService<IRetriever>();
            var ingestor = sp.GetRequiredService<IIngestor>();
            var answerEngine = sp.GetService<IAnswerEngine>();
            return new RagPipeline(retriever, ingestor, answerEngine);
        });

        var builder = new RagBuilder(services);
        configure?.Invoke(builder);

        return services;
    }
}
