using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Caching registration extension for the RAG builder. This method lived on the core
/// <c>Rag.NET</c> package's <c>RagBuilder</c> before the package decomposition; it keeps its
/// namespace so existing consumer source compiles unchanged after adding a reference to
/// <c>Rag.NET.Caching</c>.
/// </summary>
public static class CachingBuilderExtensions
{
    /// <summary>
    /// Enables two-level retrieval caching backed by <see cref="HybridCache"/>.
    /// Embedding cache caches query→embedding mappings. Result cache
    /// caches the complete post-processed result list.
    /// </summary>
    /// <remarks>
    /// Core's <c>ResultCacheBehavior</c> and <c>EmbeddingCacheBehavior</c> are always composed
    /// into the retrieval pipeline and no-op until this method registers the
    /// <see cref="HybridCache"/> implementation and <see cref="CachingOptions"/> they inject.
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseCacheEmbedding = false, UseCacheResult = false }</c>.
    /// </remarks>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">Optional delegate to configure <see cref="CachingOptions"/>.</param>
    public static TBuilder UseCaching<TBuilder>(this TBuilder builder, Action<CachingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var options = new CachingOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddHybridCache();
        return builder;
    }
}
