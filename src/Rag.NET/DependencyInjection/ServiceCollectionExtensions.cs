using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Resilience;
using Polly;
using Polly.Registry;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models.Options;
using Rag.NET.Parsers;
using Rag.NET.Pipeline;

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

        services.AddSingleton<IRagPipeline>(sp =>
        {
            var parsers = sp.GetServices<IDocumentParser>();
            var chunker = sp.GetRequiredService<IChunkingStrategy>();
            var store = sp.GetRequiredService<IVectorStore>();
            var embedder = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            var chatClient = sp.GetService<IChatClient>();
            var options = sp.GetRequiredService<ChunkingOptions>();
            var logger = sp.GetService<ILogger<RagPipeline>>();
            var resilienceProvider = sp.GetService<ResiliencePipelineProvider<string>>();
            var resilience = resilienceProvider?.GetPipeline("rag-net");

            return new RagPipeline(parsers, chunker, store, embedder, chatClient, options, logger, resilience);
        });

        var builder = new RagBuilder(services);
        configure?.Invoke(builder);

        return services;
    }
}
