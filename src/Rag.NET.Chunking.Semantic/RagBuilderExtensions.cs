using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking.Semantic;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="SemanticChunkingStrategy"/> as <see cref="IChunkingStrategy"/>,
    /// <see cref="IDocumentChunkingStrategy"/>, and <see cref="IChunkRefinementStrategy"/>.
    /// All three interfaces resolve to the same singleton instance.
    /// Uses the same <see cref="Microsoft.Extensions.AI.IEmbeddingGenerator{TInput,TEmbedding}"/> registered for retrieval
    /// by default. Override via <see cref="SemanticChunkingOptions.ChunkingEmbedder"/> for a
    /// smaller/faster model at chunking time.
    /// </summary>
    public static TBuilder UseSemanticChunking<TBuilder>(this TBuilder builder, SemanticChunkingOptions? options = null)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton(options ?? new SemanticChunkingOptions());
        builder.Services.AddSingleton<SemanticChunkingStrategy>();
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<SemanticChunkingStrategy>());
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<SemanticChunkingStrategy>());
        builder.Services.AddSingleton<IChunkRefinementStrategy>(sp => sp.GetRequiredService<SemanticChunkingStrategy>());
        return builder;
    }

    /// <summary>
    /// Registers <see cref="SemanticChunkingStrategy"/> as only <see cref="IChunkRefinementStrategy"/>.
    /// Use with <c>UseHierarchicalMerging()</c> to add semantic sub-splitting to a hierarchical pipeline
    /// without replacing the primary chunking strategy.
    /// </summary>
    public static TBuilder UseSemanticRefinement<TBuilder>(this TBuilder builder, SemanticChunkingOptions? options = null)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton(options ?? new SemanticChunkingOptions());
        builder.Services.AddSingleton<SemanticChunkingStrategy>();
        builder.Services.AddSingleton<IChunkRefinementStrategy>(sp => sp.GetRequiredService<SemanticChunkingStrategy>());
        return builder;
    }
}
