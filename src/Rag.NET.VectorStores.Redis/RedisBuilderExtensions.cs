using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using StackExchange.Redis;

namespace Rag.NET.VectorStores.Redis;

/// <summary>Registers <see cref="RedisVectorStore"/> as the pipeline's vector store.</summary>
public static class RedisBuilderExtensions
{
    /// <summary>
    /// Uses Redis (RediSearch) as the vector store, connecting with a configuration string.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configuration">
    /// A StackExchange.Redis configuration string, e.g. <c>localhost:6379</c>. The server must
    /// have the RediSearch module — Redis Stack, or Redis 8 and later, where it is built in.
    /// </param>
    /// <param name="indexName">The RediSearch index to create and query.</param>
    /// <param name="vectorDimensions">Dense embedding dimensions; must match the generator's.</param>
    /// <param name="filterableMetadataKeys">
    /// Metadata keys that may be used in <c>MetadataFilter</c>. They become case-sensitive TAG
    /// attributes in the index, so they must be known when the index is created.
    /// <b>A filter naming a key that is not declared here throws</b> rather than returning an
    /// unfiltered page — Redis is the only backend in this library that requires the declaration,
    /// because RediSearch filters only on attributes the schema names.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder UseRedis<TBuilder>(
        this TBuilder builder,
        string configuration,
        string indexName = "ragnet-idx",
        int vectorDimensions = 1536,
        IReadOnlyList<string>? filterableMetadataKeys = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Register(
            builder,
            new RedisVectorStore(configuration, indexName, vectorDimensions, filterableMetadataKeys));
    }

    /// <summary>
    /// Uses Redis as the vector store over a connection the caller already owns — the common case
    /// when Redis is present for caching and the point of this store is not standing up a second
    /// datastore. The multiplexer is not disposed by the store.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="redis">The existing connection.</param>
    /// <param name="indexName">The RediSearch index to create and query.</param>
    /// <param name="vectorDimensions">Dense embedding dimensions; must match the generator's.</param>
    /// <param name="filterableMetadataKeys">
    /// Metadata keys that may be used in <c>MetadataFilter</c>. They become case-sensitive TAG
    /// attributes in the index, so they must be known when the index is created.
    /// <b>A filter naming a key that is not declared here throws</b> rather than returning an
    /// unfiltered page — Redis is the only backend in this library that requires the declaration,
    /// because RediSearch filters only on attributes the schema names.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder UseRedis<TBuilder>(
        this TBuilder builder,
        IConnectionMultiplexer redis,
        string indexName = "ragnet-idx",
        int vectorDimensions = 1536,
        IReadOnlyList<string>? filterableMetadataKeys = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Register(
            builder, new RedisVectorStore(redis, indexName, vectorDimensions, filterableMetadataKeys));
    }

    private static TBuilder Register<TBuilder>(TBuilder builder, RedisVectorStore store)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<ICollectionManageable>(store);
        return builder;
    }
}
