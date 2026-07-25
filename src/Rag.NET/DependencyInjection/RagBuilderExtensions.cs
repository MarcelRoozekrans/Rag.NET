using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;
using Rag.NET.QueryTechniques.ContextualCompression;
using Rag.NET.Resilience;
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
    /// <para>
    /// Known limitation — persistent conversation memory: merged results carry RRF scores
    /// (roughly <c>0.033</c> at best for two stores), not similarity scores.
    /// <c>UsePersistentMemory</c> resolves the DI <see cref="IVectorStore"/> and filters
    /// recalls by <c>PersistentMemoryOptions.MinScore</c> (default 0.7) on the similarity
    /// scale, so against a federated store it would silently never recall. Point
    /// persistent memory at a dedicated store instead of the federated one until score
    /// normalization lands.
    /// </para>
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

    /// <summary>
    /// Registers the SQLite-backed <see cref="IEmbeddingVersionStore"/>
    /// (<see cref="SqliteEmbeddingVersionStore"/>) so the ingestion pipeline stamps each
    /// document with the embedding model that produced its vectors, and
    /// <c>ReindexStaleAsync</c> can find documents embedded by an older model. The model
    /// identity comes from the generator's <c>EmbeddingGeneratorMetadata</c> or the
    /// explicit <see cref="EmbeddingVersioningOptions.ModelId"/> override; when neither
    /// is available, stamping is disabled with a one-time warning. Idempotent: options
    /// and store use <c>TryAdd</c>, so the first registration wins.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">Configures the <see cref="EmbeddingVersioningOptions"/>.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="EmbeddingVersioningOptions.DatabasePath"/> is empty.
    /// </exception>
    public static TBuilder UseEmbeddingVersioning<TBuilder>(
        this TBuilder builder,
        Action<EmbeddingVersioningOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new EmbeddingVersioningOptions();
        configure?.Invoke(opts);

        if (string.IsNullOrWhiteSpace(opts.DatabasePath))
        {
            throw new ArgumentException(
                "EmbeddingVersioningOptions.DatabasePath must be a non-empty string.",
                nameof(configure));
        }

        builder.Services.TryAddSingleton(opts);
        builder.Services.TryAddSingleton<IEmbeddingVersionStore>(
            sp => new SqliteEmbeddingVersionStore(sp.GetRequiredService<EmbeddingVersioningOptions>().DatabasePath));
        return builder;
    }

    /// <summary>
    /// Registers a <see cref="FallbackChatClient"/> as the <see cref="IChatClient"/>:
    /// calls try the configured clients in order, falling through to the next on transient
    /// failures (HTTP 429/503, timeouts, rate-limit/unavailable error text) and — when
    /// <see cref="FallbackChainOptions.PerClientTimeout"/> is set — when a client exceeds
    /// the per-attempt timeout. Non-transient errors propagate immediately.
    /// </summary>
    /// <remarks>
    /// This registration supersedes any prior <see cref="IChatClient"/> registration
    /// (standard last-wins container semantics, same convention as
    /// <c>UseFederatedSearch</c>). Clients are configured as factories so each
    /// per-provider client can be built from the service provider without the chain
    /// wrapping itself — do not resolve <see cref="IChatClient"/> inside a factory:
    /// that resolves the chain recursively. Construct the provider client directly, e.g.
    /// <c>o.AddClient(sp =&gt; new OpenAIChatClient(sp.GetRequiredService&lt;OpenAIClient&gt;(), "gpt-4o"))</c>.
    /// </remarks>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">
    /// Configures the <see cref="FallbackChainOptions"/>; at least 2 clients are required.
    /// </param>
    /// <exception cref="InvalidOperationException">Thrown when fewer than 2 clients are configured.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="FallbackChainOptions.PerClientTimeout"/> is set but not positive.
    /// </exception>
    public static TBuilder UseFallbackChain<TBuilder>(this TBuilder builder, Action<FallbackChainOptions> configure)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new FallbackChainOptions();
        configure(options);

        if (options.Clients.Count < 2)
        {
            throw new InvalidOperationException(
                "UseFallbackChain requires at least 2 clients; add them via FallbackChainOptions.AddClient. " +
                "With a single client there is nothing to fall back to — register it as IChatClient directly.");
        }

        if (options.PerClientTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(configure), timeout,
                "FallbackChainOptions.PerClientTimeout must be greater than zero when set.");
        }

        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            var chain = new List<IChatClient>(options.Clients.Count);
            foreach (var factory in options.Clients)
                chain.Add(factory(sp));

            return new FallbackChatClient(chain, sp.GetService<ILogger<FallbackChatClient>>(), options.PerClientTimeout);
        });
        return builder;
    }
}
