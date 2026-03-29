using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;

namespace Rag.NET.Raptor;

/// <summary>Extension methods for registering RAPTOR in the Rag.NET pipeline.</summary>
public static class RagBuilderExtensions
{
    /// <summary>
    /// Enables RAPTOR — recursive abstractive tree-organized retrieval.
    /// Registers <see cref="RaptorIngestionBehavior"/> and <see cref="RaptorRetrievalBehavior"/>
    /// into the pipeline.
    /// </summary>
    public static RagBuilder UseRaptor(
        this RagBuilder builder,
        Action<RaptorOptions>? configure = null,
        Action<RaptorRetrievalOptions>? retrieval = null)
    {
        var options = new RaptorOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        var retrievalOptions = new RaptorRetrievalOptions();
        retrieval?.Invoke(retrievalOptions);
        builder.Services.AddSingleton(retrievalOptions);

        builder.Services.AddSingleton<RaptorIngestionBehavior>(sp =>
            new RaptorIngestionBehavior(
                options.SummaryChatClient ?? sp.GetRequiredService<IChatClient>(),
                options.SummaryEmbedder ?? sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                options));

        builder.Services.AddSingleton<RaptorRetrievalBehavior>(sp =>
            new RaptorRetrievalBehavior(sp.GetRequiredService<RaptorRetrievalOptions>()));

        return builder;
    }
}
