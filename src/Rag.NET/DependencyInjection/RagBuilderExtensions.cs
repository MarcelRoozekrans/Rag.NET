using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
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
    /// Persistent conversation memory: merged results carry RRF scores (roughly
    /// <c>0.033</c> at best for two stores), not similarity scores, so
    /// <see cref="FederatedVectorStore"/> declares
    /// <see cref="IScoreScaleAware"/> with <see cref="ScoreScale.OpaqueRanking"/>.
    /// <c>UsePersistentMemory</c> probes that and skips
    /// <c>PersistentMemoryOptions.MinScore</c> (default 0.7) rather than filtering every
    /// recall away: it injects the top <c>TopK</c> matches in rank order and warns once
    /// per memory instance. Recall works, but a minimum relevance cannot be enforced
    /// against a federated store — lower <c>TopK</c>, or point persistent memory at a
    /// dedicated similarity-scaled store when a real threshold matters.
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
            {
                if (factory is null)
                {
                    throw new ArgumentException(
                        "FallbackChainOptions.Clients contains a null factory. Add clients via " +
                        "FallbackChainOptions.AddClient(...), which rejects null, instead of mutating Clients directly.",
                        nameof(configure));
                }

                chain.Add(factory(sp));
            }

            return new FallbackChatClient(chain, sp.GetService<ILogger<FallbackChatClient>>(), options.PerClientTimeout);
        });
        return builder;
    }

    /// <summary>Service key of the chat-surface <see cref="IRateLimiter"/> registered by <c>UseRateLimiting</c>.</summary>
    internal const string ChatRateLimiterKey = "ragnet.ratelimit.chat";

    /// <summary>Service key of the embedding-surface <see cref="IRateLimiter"/> registered by <c>UseRateLimiting</c>.</summary>
    internal const string EmbeddingRateLimiterKey = "ragnet.ratelimit.embedding";

    /// <summary>
    /// Wraps the registered <see cref="IChatClient"/> and/or
    /// <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> with rate-limiting
    /// decorators backed by a token bucket: calls over the configured per-minute budget
    /// wait for a permit (they are only rejected when
    /// <see cref="RateLimitingOptions.MaxQueuedRequests"/> is set and the wait queue is full).
    /// </summary>
    /// <remarks>
    /// Each configured surface gets its own independent limiter. A streaming chat call
    /// acquires one permit before the stream starts; an embedding call acquires one permit
    /// per call regardless of how many values it embeds. Ordering matters: this extension
    /// decorates whatever is registered when it runs, so register the underlying client
    /// (provider registration, <c>UseFallbackChain</c>, …) before calling
    /// <c>UseRateLimiting</c> — a configured surface with no underlying registration fails
    /// at registration time, before either surface is decorated (no half-applied state).
    /// Idempotent per surface: a surface whose limiter is already registered keeps its
    /// first configuration (same first-wins convention as <c>UseEmbeddingVersioning</c>) —
    /// budgets are never stacked or re-applied; a repeat call only adds surfaces that were
    /// not configured before.
    /// </remarks>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">
    /// Configures the <see cref="RateLimitingOptions"/>; at least one surface budget is required.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no surface budget is configured, or when a configured surface has no
    /// underlying registration to decorate.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a configured value is not positive.</exception>
    public static TBuilder UseRateLimiting<TBuilder>(this TBuilder builder, Action<RateLimitingOptions> configure)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RateLimitingOptions();
        configure(options);
        ValidateRateLimitingOptions(options, nameof(configure));
        int? maxQueuedRequests = options.MaxQueuedRequests;

        // Idempotence per surface: an already-limited surface keeps its first configuration.
        bool limitChat = options.ChatRequestsPerMinute is not null
            && !ContainsServiceKey(builder.Services, ChatRateLimiterKey);
        bool limitEmbedding = options.EmbeddingRequestsPerMinute is not null
            && !ContainsServiceKey(builder.Services, EmbeddingRateLimiterKey);

        // Pre-check every surface to be decorated BEFORE decorating any, so a missing
        // registration throws without leaving the other surface half-applied.
        if (limitChat)
        {
            ServiceDecorationHelper.EnsureRegistered<IChatClient>(builder.Services);
        }

        if (limitEmbedding)
        {
            ServiceDecorationHelper.EnsureRegistered<IEmbeddingGenerator<string, Embedding<float>>>(builder.Services);
        }

        if (limitChat)
        {
            int chatRpm = options.ChatRequestsPerMinute!.Value;
            builder.Services.AddKeyedSingleton<IRateLimiter>(ChatRateLimiterKey,
                (_, _) => new TokenBucketRateLimiterAdapter(chatRpm, "chat", maxQueuedRequests));
            ServiceDecorationHelper.Decorate<IChatClient>(builder.Services, (inner, sp) =>
                new RateLimitedChatClient(inner, sp.GetRequiredKeyedService<IRateLimiter>(ChatRateLimiterKey)));
        }

        if (limitEmbedding)
        {
            int embeddingRpm = options.EmbeddingRequestsPerMinute!.Value;
            builder.Services.AddKeyedSingleton<IRateLimiter>(EmbeddingRateLimiterKey,
                (_, _) => new TokenBucketRateLimiterAdapter(embeddingRpm, "embedding", maxQueuedRequests));
            ServiceDecorationHelper.Decorate<IEmbeddingGenerator<string, Embedding<float>>>(builder.Services, (inner, sp) =>
                new RateLimitedEmbeddingGenerator(inner, sp.GetRequiredKeyedService<IRateLimiter>(EmbeddingRateLimiterKey)));
        }

        return builder;
    }

    /// <summary>Sentinel service key marking that <c>UseCostBudgeting</c> has been applied.</summary>
    internal const string CostBudgetingAppliedKey = "ragnet.costbudget.applied";

    /// <summary>
    /// Wraps the registered <see cref="IChatClient"/> and/or
    /// <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> with cost-tracking
    /// decorators: every call is gated on the configured daily/monthly spend limits
    /// (throwing <see cref="BudgetExceededException"/> once a limit is reached) and its
    /// token usage and cost — priced with the user-supplied
    /// <see cref="CostBudgetOptions"/> rates — are recorded to an <see cref="ICostLedger"/>.
    /// </summary>
    /// <remarks>
    /// The ledger defaults to the SQLite-backed <see cref="SqliteCostLedger"/> at
    /// <see cref="CostBudgetOptions.DatabasePath"/> and is registered with <c>TryAdd</c>:
    /// an <see cref="ICostLedger"/> registered BEFORE this call (e.g.
    /// <see cref="InMemoryCostLedger"/> or a custom store) wins. Each registered surface
    /// is decorated; at least one must be registered before this call (same ordering rule
    /// as <c>UseRateLimiting</c>) — a surface registered afterwards is not tracked.
    /// Idempotent: repeated calls are no-ops keyed on a sentinel registration, so
    /// decorators never stack and the first configuration wins (same first-wins
    /// convention as <c>UseRateLimiting</c>). The budget gate is pre-call: every call
    /// admitted before a limit is reached runs to completion, so the overshoot can be
    /// several in-flight calls' worth under concurrency — parallel ingestion routinely has
    /// N embedding batches in flight — and limits should be sized with headroom for your
    /// concurrency level.
    /// </remarks>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">
    /// Configures the <see cref="CostBudgetOptions"/>; at least one of
    /// <see cref="CostBudgetOptions.DailyLimit"/>/<see cref="CostBudgetOptions.MonthlyLimit"/>
    /// is required.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no limit is configured, or when neither surface has an underlying
    /// registration to decorate.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a price or limit is negative.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="CostBudgetOptions.DatabasePath"/> is empty.</exception>
    public static TBuilder UseCostBudgeting<TBuilder>(this TBuilder builder, Action<CostBudgetOptions> configure)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CostBudgetOptions();
        configure(options);
        ValidateCostBudgetOptions(options, nameof(configure));

        // Idempotence: repeated UseCostBudgeting must not stack decorators (first-wins,
        // mirroring UseRateLimiting). A sentinel key is used instead of probing for the
        // ICostLedger registration because a user-registered custom ledger would otherwise
        // read as "already applied" and silently skip decoration.
        if (ContainsServiceKey(builder.Services, CostBudgetingAppliedKey))
        {
            return builder;
        }

        bool trackChat = ServiceDecorationHelper.IsRegistered<IChatClient>(builder.Services);
        bool trackEmbedding = ServiceDecorationHelper.IsRegistered<IEmbeddingGenerator<string, Embedding<float>>>(builder.Services);
        if (!trackChat && !trackEmbedding)
        {
            throw new InvalidOperationException(
                "UseCostBudgeting found no IChatClient or IEmbeddingGenerator registration to decorate. " +
                "Register the underlying client/generator (provider registration, UseFallbackChain, …) " +
                "before calling UseCostBudgeting — decoration wraps whatever is registered at that point.");
        }

        builder.Services.AddKeyedSingleton<object>(CostBudgetingAppliedKey, (_, _) => new object());
        // TryAdd: a custom/in-memory ICostLedger registered earlier wins over the SQLite default.
        builder.Services.TryAddSingleton<ICostLedger>(sp =>
            new SqliteCostLedger(options.DatabasePath, sp.GetService<TimeProvider>() ?? TimeProvider.System));

        if (trackChat)
        {
            ServiceDecorationHelper.Decorate<IChatClient>(builder.Services, (inner, sp) =>
                new CostTrackingChatClient(inner, sp.GetRequiredService<ICostLedger>(), options,
                    sp.GetService<ILogger<CostTrackingChatClient>>()));
        }

        if (trackEmbedding)
        {
            ServiceDecorationHelper.Decorate<IEmbeddingGenerator<string, Embedding<float>>>(builder.Services, (inner, sp) =>
                new CostTrackingEmbeddingGenerator(inner, sp.GetRequiredService<ICostLedger>(), options,
                    sp.GetService<ILogger<CostTrackingEmbeddingGenerator>>()));
        }

        return builder;
    }

    private static void ValidateCostBudgetOptions(CostBudgetOptions options, string paramName)
    {
        if (options.DailyLimit is null && options.MonthlyLimit is null)
        {
            throw new InvalidOperationException(
                "UseCostBudgeting requires at least one limit: set CostBudgetOptions.DailyLimit " +
                "and/or CostBudgetOptions.MonthlyLimit.");
        }

        ThrowIfNegative(options.InputPricePerMTokens, nameof(CostBudgetOptions.InputPricePerMTokens), paramName);
        ThrowIfNegative(options.OutputPricePerMTokens, nameof(CostBudgetOptions.OutputPricePerMTokens), paramName);
        ThrowIfNegative(options.EmbeddingPricePerMTokens, nameof(CostBudgetOptions.EmbeddingPricePerMTokens), paramName);
        // A zero limit is allowed on purpose: it is an explicit kill switch (block all calls).
        ThrowIfNegative(options.DailyLimit ?? 0m, nameof(CostBudgetOptions.DailyLimit), paramName);
        ThrowIfNegative(options.MonthlyLimit ?? 0m, nameof(CostBudgetOptions.MonthlyLimit), paramName);

        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            throw new ArgumentException(
                "CostBudgetOptions.DatabasePath must be a non-empty string.", paramName);
        }
    }

    private static void ThrowIfNegative(decimal value, string propertyName, string paramName)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(paramName, value,
                $"CostBudgetOptions.{propertyName} must not be negative.");
        }
    }

    private static bool ContainsServiceKey(IServiceCollection services, string serviceKey)
    {
        for (int i = 0; i < services.Count; i++)
        {
            if (services[i].IsKeyedService && Equals(services[i].ServiceKey, serviceKey))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateRateLimitingOptions(RateLimitingOptions options, string paramName)
    {
        if (options.ChatRequestsPerMinute is null && options.EmbeddingRequestsPerMinute is null)
        {
            throw new InvalidOperationException(
                "UseRateLimiting requires at least one surface budget: set RateLimitingOptions.ChatRequestsPerMinute " +
                "and/or RateLimitingOptions.EmbeddingRequestsPerMinute.");
        }

        ThrowIfNonPositive(options.ChatRequestsPerMinute, nameof(RateLimitingOptions.ChatRequestsPerMinute), paramName);
        ThrowIfNonPositive(options.EmbeddingRequestsPerMinute, nameof(RateLimitingOptions.EmbeddingRequestsPerMinute), paramName);
        ThrowIfNonPositive(options.MaxQueuedRequests, nameof(RateLimitingOptions.MaxQueuedRequests), paramName);
    }

    private static void ThrowIfNonPositive(int? value, string propertyName, string paramName)
    {
        if (value is <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value,
                $"RateLimitingOptions.{propertyName} must be greater than zero when set.");
        }
    }
}
