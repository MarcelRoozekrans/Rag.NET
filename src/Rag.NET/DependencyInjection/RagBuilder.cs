using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Resilience;
using Polly;
using Polly.Registry;
using Polly.Retry;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Memory;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Resilience;
using Rag.NET.SelfQuery;
using Rag.NET.Retrieval;
using Rag.NET.Search;
using Rag.NET.Storage;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Fluent builder for configuring the Rag.NET pipeline services.
/// Obtain an instance via <c>services.AddRagNet(rag => ...)</c>.
/// </summary>
public sealed class RagBuilder(IServiceCollection services) : IRagBuilder
{
    /// <summary>Gets the underlying <see cref="IServiceCollection"/> for advanced registrations.</summary>
    public IServiceCollection Services { get; } = services;

    /// <summary>
    /// Registers a custom chunking strategy. Optionally configures <see cref="ChunkingOptions"/>
    /// (MaxChunkSize and Overlap), which are interpreted as characters by most built-in strategies.
    /// </summary>
    /// <typeparam name="TStrategy">The <see cref="IChunkingStrategy"/> implementation to use.</typeparam>
    /// <param name="configure">Optional delegate to configure chunking options.</param>
    public RagBuilder UseChunkingStrategy<TStrategy>(Action<ChunkingOptions>? configure = null)
        where TStrategy : class, IChunkingStrategy
    {
        Services.AddSingleton<IChunkingStrategy, TStrategy>();

        if (configure is not null)
        {
            var options = new ChunkingOptions();
            configure(options);
            Services.AddSingleton(options);
        }

        return this;
    }

    /// <summary>
    /// Registers a document parser. Multiple parsers can be registered; the pipeline
    /// selects the first one whose <c>CanParse</c> returns <see langword="true"/> for a given content type.
    /// </summary>
    /// <typeparam name="TParser">The <see cref="IDocumentParser"/> implementation to register.</typeparam>
    public RagBuilder AddParser<TParser>() where TParser : class, IDocumentParser
    {
        Services.AddSingleton<IDocumentParser, TParser>();
        return this;
    }

    /// <inheritdoc/>
    IRagBuilder IRagBuilder.AddParser<TParser>() => AddParser<TParser>();

    /// <inheritdoc/>
    IRagBuilder IRagBuilder.UseReranking<TReranker>() => UseReranking<TReranker>();

    /// <summary>
    /// Wraps the registered <see cref="IRetriever"/> with <see cref="DeepResearchRetriever"/>.
    /// On each retrieval call, runs a sufficiency-gated loop: retrieve, ask the LLM whether the
    /// result is sufficient, and if not generate focused sub-queries and retrieve again.
    /// Results are merged and deduplicated across all iterations.
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> to be registered in DI.
    /// The decorator is wired by <c>AddRagNet</c> after the
    /// builder delegate returns — calling this method outside of <c>AddRagNet</c>'s configure
    /// delegate has no effect.
    /// </remarks>
    /// <param name="options">Optional options; defaults to <see cref="DeepResearchOptions"/> defaults.</param>
    public RagBuilder UseDeepResearch(DeepResearchOptions? options = null)
    {
        Services.AddSingleton(options ?? new DeepResearchOptions());
        return this;
    }

    /// <summary>
    /// Registers <see cref="Rag.NET.Retrieval.TagRetriever"/> as a decorator over the existing
    /// <see cref="IRetriever"/>. At query time, the decorator embeds the query, cosine-scans
    /// the tag index populated during ingestion, and injects matching tag key-value pairs
    /// as <see cref="Rag.NET.Models.Options.RetrievalOptions.MetadataFilter"/> entries.
    /// Requires <c>IEmbeddingGenerator</c> to be registered.
    /// </summary>
    /// <remarks>
    /// The decorator is wired by <c>AddRagNet</c> after the builder delegate returns.
    /// When both <c>UseDeepResearch</c> and <c>UseTagRetrieval</c> are configured,
    /// the stacking order is <c>TagRetriever → DeepResearchRetriever → PipelineRetriever</c>.
    /// </remarks>
    public RagBuilder UseTagRetrieval(TagRetrievalOptions? options = null)
    {
        Services.AddSingleton(options ?? new TagRetrievalOptions());
        Services.TryAddSingleton<ITagIndex, InMemoryTagIndex>();
        return this;
    }

    /// <summary>
    /// Registers <see cref="Rag.NET.Retrieval.TimeWeightedRetriever"/> as a decorator over the
    /// existing <see cref="IRetriever"/>. After retrieval, each result's similarity score is
    /// multiplied by <c>e^(−DecayRate × age_hours)</c> where age is derived from
    /// <c>chunk.Metadata["created_at"]</c> written at ingest time by
    /// <see cref="Rag.NET.Ingestion.Behaviors.MetadataBehavior"/>.
    /// Results are re-sorted by the combined score before being returned.
    /// </summary>
    /// <remarks>
    /// The decorator is wired by <c>AddRagNet</c> after the builder delegate returns.
    /// When combined with other decorators, stacking order (outermost first) is:
    /// <c>TagRetriever → TimeWeightedRetriever → DeepResearchRetriever → PipelineRetriever</c>.
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseTimeWeighting = false }</c>.
    /// </remarks>
    public RagBuilder UseTimeWeighting(TimeWeightedOptions? options = null)
    {
        Services.AddSingleton(options ?? new TimeWeightedOptions());
        return this;
    }

    /// <summary>
    /// Enables LLM-driven metadata extraction at ingestion time.
    /// When registered, an LLM call is made per chunk to extract structured key-value tags,
    /// which are stored in chunk metadata for use with <see cref="UseSelfQuery"/>.
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> to be registered in DI.
    /// When <paramref name="schema"/> is provided, extraction is constrained to the listed fields.
    /// </remarks>
    /// <param name="schema">Optional list of fields to extract. When null, the LLM extracts freely.</param>
    public RagBuilder UseLlmMetadataExtraction(IReadOnlyList<AttributeInfo>? schema = null)
    {
        Services.AddSingleton(new LlmMetadataExtractionOptions { Schema = schema });
        return this;
    }

    /// <summary>
    /// Enables self-query rewriting at retrieval time.
    /// When registered, the LLM parses each question into a refined semantic query
    /// and a structured metadata filter before retrieval executes.
    /// </summary>
    /// <remarks>
    /// Requires <c>IChatClient</c> to be registered in DI.
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseSelfQuery = false }</c>.
    /// When <paramref name="schema"/> is provided, filtering is constrained to the listed fields.
    /// </remarks>
    /// <param name="schema">Optional list of filterable fields. When null, the LLM filters freely.</param>
    public RagBuilder UseSelfQuery(IReadOnlyList<AttributeInfo>? schema = null)
    {
        Services.AddSingleton(new SelfQueryOptions { Schema = schema });
        return this;
    }

    /// <summary>
    /// Enables two-level retrieval caching backed by <see cref="HybridCache"/>.
    /// Embedding cache caches query→embedding mappings. Result cache
    /// caches the complete post-processed result list.
    /// </summary>
    /// <remarks>
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseCacheEmbedding = false, UseCacheResult = false }</c>.
    /// </remarks>
    /// <param name="configure">Optional delegate to configure <see cref="CachingOptions"/>.</param>
    public RagBuilder UseCaching(Action<CachingOptions>? configure = null)
    {
        var options = new CachingOptions();
        configure?.Invoke(options);
        Services.AddSingleton(options);
        Services.AddHybridCache();
        return this;
    }

    /// <summary>
    /// Enables parent-document retrieval. At ingestion, documents are chunked twice:
    /// small child chunks are embedded for precise matching, large parent chunks are
    /// stored in-memory for context-rich answer generation. At retrieval, child matches
    /// are replaced with their parent text.
    /// </summary>
    /// <remarks>
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseParentDocument = false }</c>.
    /// </remarks>
    /// <param name="configure">Optional delegate to configure <see cref="ParentDocumentOptions"/>.</param>
    public RagBuilder UseParentDocumentRetrieval(Action<ParentDocumentOptions>? configure = null)
    {
        var options = new ParentDocumentOptions();
        configure?.Invoke(options);
        Services.AddSingleton(options);
        Services.AddSingleton<InMemoryParentChunkStore>();
        Services.TryAddSingleton<IParentChunkStore>(sp => sp.GetRequiredService<InMemoryParentChunkStore>());
        return this;
    }

    /// <summary>
    /// Registers <typeparamref name="TReranker"/> as the <see cref="IReranker"/>.
    /// When registered, <see cref="RagPipeline"/> rescores search results using
    /// the cross-encoder for higher precision ranking.
    /// </summary>
    /// <remarks>
    /// Per-call opt-out: pass <c>new RetrievalOptions { UseReranking = false }</c>.
    /// Over-fetch control: set <c>RetrievalOptions.CandidateCount</c> (defaults to TopK * 3).
    /// </remarks>
    public RagBuilder UseReranking<TReranker>() where TReranker : class, IReranker
    {
        Services.AddSingleton<IReranker, TReranker>();
        return this;
    }

    /// <summary>
    /// Registers <see cref="MmrRetriever"/> in the post-retrieval chain.
    /// When registered, MMR selection is opt-in per call: set
    /// <c>new RetrievalOptions { UseMmr = true }</c> to activate.
    /// </summary>
    /// <remarks>
    /// MMR over-fetches candidates (<see cref="RetrievalOptions.MmrCandidateCount"/>, default TopK × 3),
    /// then selects <see cref="RetrievalOptions.TopK"/> results balancing relevance and diversity.
    /// Requires <c>IEmbeddingGenerator</c> to be registered in DI.
    /// Per-call activation: pass <c>new RetrievalOptions { UseMmr = true }</c>.
    /// </remarks>
    public RagBuilder UseMmr()
    {
        Services.AddSingleton<MmrEnabled>();
        return this;
    }

    /// <summary>
    /// Registers SQLite-backed persistence for <see cref="IBm25Index"/> and <see cref="IParentChunkStore"/>.
    /// On startup, both stores load persisted data from <paramref name="dbPath"/>.
    /// Every Add/Remove writes through to SQLite synchronously.
    /// </summary>
    /// <param name="dbPath">Path to the SQLite database file. Created if it does not exist.</param>
    /// <param name="collectionName">
    /// Optional stale-data guard. If the registered name differs from what is stored in the database,
    /// all persisted data is wiped before loading. Change this value when replacing the vector store.
    /// Omit to skip the stale guard.
    /// </param>
    public RagBuilder UseSqlitePersistence(string dbPath, string? collectionName = null)
    {
        Services.AddSingleton<SqliteBm25Index>(sp => new SqliteBm25Index(dbPath, collectionName, sp.GetService<SynonymMap>()));
        Services.AddSingleton<IBm25Index>(sp => sp.GetRequiredService<SqliteBm25Index>());

        Services.AddSingleton<SqliteParentChunkStore>(_ => new SqliteParentChunkStore(dbPath, collectionName));
        Services.AddSingleton<IParentChunkStore>(sp => sp.GetRequiredService<SqliteParentChunkStore>());

        return this;
    }

    /// <summary>
    /// Registers a <see cref="SynonymMap"/> that expands tokens at both BM25 index time and query time.
    /// Synonyms are bidirectional: any term in a group matches all other terms in that group.
    /// The map is a singleton — call <see cref="SynonymMap.AddGroup"/> or
    /// <see cref="SynonymMap.RemoveGroup"/> at runtime for live updates without restart.
    /// </summary>
    public RagBuilder UseBm25Synonyms(SynonymMap synonymMap)
    {
        Services.AddSingleton(synonymMap);
        return this;
    }

    /// <summary>
    /// Registers <see cref="SqliteContentHashStore"/> as the <see cref="IContentHashStore"/>.
    /// When registered, <see cref="RagPipelineExtensions.IngestFromProviderAsync"/> automatically skips
    /// files that have not changed since the last ingestion run.
    /// </summary>
    /// <param name="dbPath">Path to the SQLite file. Created if it does not exist.</param>
    public RagBuilder UseContentHashRecordManager(string dbPath)
    {
        Services.AddSingleton<IContentHashStore>(_ => new SqliteContentHashStore(dbPath));
        return this;
    }

    /// <summary>Name of the Polly resilience pipeline registered by <see cref="ConfigureResilience"/>.</summary>
    internal const string ResiliencePipelineName = "rag-net";

    /// <summary>Sentinel service key marking that <see cref="ConfigureResilience"/> has decorated the surfaces.</summary>
    internal const string ResilienceAppliedKey = "ragnet.resilience.applied";

    /// <summary>
    /// Registers a Polly resilience pipeline named <c>"rag-net"</c> and wraps the registered
    /// <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> and
    /// <see cref="IVectorStore"/> with decorators that execute every call through it.
    /// When no <paramref name="configure"/> delegate is provided, a default exponential back-off retry
    /// (3 attempts, 1 s base delay, jitter) is configured.
    /// </summary>
    /// <remarks>
    /// Only the surfaces registered at the time of the call are decorated — ordering matters
    /// (same rule as <c>UseRateLimiting</c>/<c>UseCostBudgeting</c>): register the store and
    /// the embedding generator first, e.g. <c>rag.UsePgVector(cs).ConfigureResilience()</c>.
    /// Not calling this method leaves the container graph completely undecorated.
    /// Idempotent: repeated calls re-configure the named pipeline (last wins) but never stack a
    /// second decorator layer.
    /// <para>
    /// Cancellation passes through untouched: the caller's token flows into every attempt and the
    /// default retry predicate excludes <see cref="OperationCanceledException"/>, so a cancelled
    /// call (and, deliberately, an <c>HttpClient</c> timeout surfacing as
    /// <see cref="TaskCanceledException"/>) is never retried. <see cref="BudgetExceededException"/>
    /// is excluded for the same reason — it is a kill switch, not a provider blip. A custom
    /// <paramref name="configure"/> owns its own predicates and should exclude both too.
    /// </para>
    /// <para>
    /// Composition with <c>UseRateLimiting</c>/<c>UseCostBudgeting</c>: those decorate the same
    /// embedding surface, so call this method <em>after</em> them to make retry the outermost
    /// layer — each retried attempt then re-acquires a rate permit and is recorded to the cost
    /// ledger, which is what a real second API call costs.
    /// </para>
    /// <para>
    /// Double retry: the Weaviate and Chroma stores hand-build a retry-only
    /// <c>ResilienceHandler</c> on their own <c>HttpClient</c> (a bare
    /// <c>AddRetry(new HttpRetryStrategyOptions())</c> pipeline — not
    /// <c>AddStandardResilienceHandler</c>, so no transport-level timeout, circuit breaker or
    /// concurrency limiter). For those stores this decorator stacks on top of transport-level
    /// retries and attempts multiply: both layers default to <c>MaxRetryAttempts = 3</c>, which
    /// Polly counts as retries, so each layer makes 1 + 3 = 4 attempts and the worst case is
    /// 4 × 4 = up to 16 requests. Configure one layer or the other.
    /// </para>
    /// </remarks>
    /// <param name="configure">
    /// Optional delegate to customise the <see cref="ResiliencePipelineBuilder"/>.
    /// Replaces the default retry policy when supplied.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when neither an <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> nor an
    /// <see cref="IVectorStore"/> is registered yet — there would be nothing to apply the pipeline to.
    /// </exception>
    public RagBuilder ConfigureResilience(Action<ResiliencePipelineBuilder>? configure = null)
    {
        // Idempotence: a repeat call must not stack a second decorator layer (first-wins,
        // mirroring UseRateLimiting/UseCostBudgeting). The named pipeline is still
        // re-registered so the latest configuration wins for the already-wired decorators.
        bool alreadyApplied = Services.Any(static d =>
            d.IsKeyedService && Equals(d.ServiceKey, ResilienceAppliedKey));

        bool decorateEmbedding = !alreadyApplied
            && ServiceDecorationHelper.IsRegistered<IEmbeddingGenerator<string, Embedding<float>>>(Services);
        bool decorateStore = !alreadyApplied && ServiceDecorationHelper.IsRegistered<IVectorStore>(Services);

        // Validate before mutating the collection, so a misordered call leaves no half-applied state.
        if (!alreadyApplied && !decorateEmbedding && !decorateStore)
        {
            throw new InvalidOperationException(
                "ConfigureResilience found no IEmbeddingGenerator or IVectorStore registration to apply " +
                "the \"rag-net\" pipeline to. Register the vector store and/or embedding generator " +
                "(UsePgVector, UseQdrant, your embedding provider, …) before calling ConfigureResilience — " +
                "it wraps whatever is registered at that point.");
        }

        Services.AddResiliencePipeline(ResiliencePipelineName, builder => BuildPipeline(builder, configure));

        if (alreadyApplied)
        {
            return this;
        }

        Services.AddKeyedSingleton<object>(ResilienceAppliedKey, static (_, _) => new object());

        if (decorateEmbedding)
        {
            ServiceDecorationHelper.Decorate<IEmbeddingGenerator<string, Embedding<float>>>(
                Services, static (inner, sp) => new ResilientEmbeddingGenerator(inner, ResolvePipeline(sp)));
        }

        if (decorateStore)
        {
            ServiceDecorationHelper.Decorate<IVectorStore>(
                Services, static (inner, sp) => ResilientVectorStore.Create(inner, ResolvePipeline(sp)));
        }

        return this;
    }

    private static ResiliencePipeline ResolvePipeline(IServiceProvider serviceProvider) =>
        serviceProvider.GetRequiredService<ResiliencePipelineProvider<string>>()
            .GetPipeline(ResiliencePipelineName);

    private static void BuildPipeline(
        ResiliencePipelineBuilder builder,
        Action<ResiliencePipelineBuilder>? configure)
    {
        if (configure is not null)
        {
            configure(builder);
            return;
        }

        builder.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            // Polly's default predicate handles every exception. Two exclusions:
            // (1) Cancellation must pass through untouched — a caller's
            //     OperationCanceledException is never a transient failure, so it is excluded
            //     here rather than relying on Polly's internal token check (which only covers
            //     an already-signalled token).
            // (2) BudgetExceededException is a deliberate kill switch, not a provider blip;
            //     retrying it would burn the back-off budget on a call that cannot succeed
            //     (the same non-transient-by-type pin FallbackChatClient applies).
            ShouldHandle = static args => ValueTask.FromResult(
                args.Outcome.Exception is not null
                    and not OperationCanceledException
                    and not BudgetExceededException),
        });
    }

    /// <summary>
    /// Registers <see cref="ConversationMemoryPipeline"/> as the <see cref="IConversationMemory"/>.
    /// When registered, answer engines automatically trim conversation history before each call
    /// using the configured sliding-window, token-budget, and optional summary strategies.
    /// Use the optional <paramref name="configure"/> delegate to wrap the pipeline with additional
    /// decorators (e.g. <c>Rag.NET.Memory.RagBuilderExtensions.UsePersistentMemory</c>).
    /// </summary>
    /// <param name="options">Optional memory options. Defaults to pass-through (no trimming).</param>
    /// <param name="configure">Optional delegate to configure memory decorators.</param>
    public RagBuilder UseConversationMemory(
        ConversationMemoryOptions? options = null,
        Action<ConversationMemoryBuilder>? configure = null)
    {
        var opts = options ?? new ConversationMemoryOptions();
        Services.AddSingleton(opts);

        var memBuilder = new ConversationMemoryBuilder(Services);
        configure?.Invoke(memBuilder);

        if (memBuilder.DecoratorFactory is { } factory)
        {
            Services.AddSingleton<IConversationMemory>(sp =>
            {
                IConversationMemory pipeline = new ConversationMemoryPipeline(
                    opts,
                    sp.GetService<IChatClient>(),
                    sp.GetService<ILogger<ConversationMemoryPipeline>>() ?? NullLogger<ConversationMemoryPipeline>.Instance);
                return factory(sp, pipeline);
            });
        }
        else
        {
            Services.AddSingleton<IConversationMemory>(sp =>
                new ConversationMemoryPipeline(
                    opts,
                    sp.GetService<IChatClient>(),
                    sp.GetService<ILogger<ConversationMemoryPipeline>>() ?? NullLogger<ConversationMemoryPipeline>.Instance));
        }

        return this;
    }
}
