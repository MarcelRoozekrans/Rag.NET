using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.QueryTechniques.ContextualCompression;
using Rag.NET.Retrieval.Behaviors;
using Rag.NET.Storage;

namespace Rag.NET.DependencyInjection;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers a <see cref="FederatedVectorStore"/> as the <see cref="IVectorStore"/>:
    /// searches fan out to every store added via
    /// <see cref="FederatedStoreBuilder.AddStore"/> and are merged with Reciprocal Rank
    /// Fusion; writes and deletes go to the primary store only
    /// (<see cref="FederatedStoreBuilder.WithPrimary"/>, default the first store).
    /// The rest of the pipeline (MMR, reranking, caching, …) composes unchanged.
    /// </summary>
    /// <remarks>
    /// This registration supersedes any prior <see cref="IVectorStore"/> registration
    /// (standard last-wins container semantics): do not combine with
    /// <c>UsePgVector</c>/<c>UseQdrant</c>-style calls — add those stores through the
    /// builder instead, e.g. <c>f.AddStore(_ =&gt; new PgVectorStore(...), "pg")</c>.
    /// Federation is dense-only: capability interfaces of the underlying stores
    /// (<c>IHybridSearchable</c>, <c>ICollectionManageable</c>, sparse search) are not
    /// federated and keep pointing at whatever registered them.
    /// </remarks>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">Configures the federated stores; at least 2 are required.</param>
    public static TBuilder UseFederatedSearch<TBuilder>(this TBuilder builder, Action<FederatedStoreBuilder> configure)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(configure);

        var federationBuilder = new FederatedStoreBuilder();
        configure(federationBuilder);
        federationBuilder.Validate();

        builder.Services.AddSingleton<IVectorStore>(federationBuilder.Build);
        return builder;
    }

    /// <summary>
    /// Opt-in: inserts <see cref="ContextualCompressionRetrievalBehavior"/> into the retrieval pipeline
    /// so plain <c>RetrieveAsync</c> callers receive compressed text (not just <c>AskAsync</c>).
    /// Requires <c>UseContextualCompression</c> (from <c>Rag.NET.QueryTechniques</c>) to have been called first.
    /// </summary>
    /// <remarks>
    /// Inserted before <see cref="RetrievalGuardBehavior"/> so compression sees post-reranking results
    /// but before any guard filtering. Use <c>AddRagNet</c> first so the retrieval pipeline builder
    /// is available in DI.
    /// </remarks>
    /// <param name="builder">The RAG builder.</param>
    public static TBuilder UseContextualCompressionInRetrieval<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        if (!builder.Services.Any(d => d.ServiceType == typeof(IContextualCompressor)))
        {
            throw new InvalidOperationException(
                "UseContextualCompressionInRetrieval requires UseContextualCompression to be called first.");
        }

        var pipelineBuilder = builder.Services
            .FirstOrDefault(d => d.ServiceType == typeof(RetrievalPipelineBuilder))
            ?.ImplementationInstance as RetrievalPipelineBuilder
            ?? throw new InvalidOperationException(
                "UseContextualCompressionInRetrieval requires AddRagNet to be called first so that " +
                "RetrievalPipelineBuilder is registered in DI.");

        // Idempotency guard: avoid inserting the behavior twice when the extension is called
        // multiple times (e.g., from layered composition roots).
        if (pipelineBuilder.GetBehaviorTypes().Contains(typeof(ContextualCompressionRetrievalBehavior)))
        {
            return builder;
        }

        pipelineBuilder.Add<ContextualCompressionRetrievalBehavior>(before: typeof(RetrievalGuardBehavior));

        return builder;
    }
}
