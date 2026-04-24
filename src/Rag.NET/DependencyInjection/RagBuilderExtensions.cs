using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.QueryTechniques.ContextualCompression;
using Rag.NET.Retrieval.Behaviors;

namespace Rag.NET.DependencyInjection;

public static class RagBuilderExtensions
{
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

        pipelineBuilder.Add<ContextualCompressionRetrievalBehavior>(before: typeof(RetrievalGuardBehavior));

        return builder;
    }
}
