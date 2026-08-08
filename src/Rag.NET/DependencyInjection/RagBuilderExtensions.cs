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
    /// The ledger defaults to the in-memory <see cref="InMemoryCostLedger"/> and is registered
    /// with <c>TryAdd</c>: an <see cref="ICostLedger"/> registered BEFORE this call (e.g. the
    /// persistent <c>UseSqliteCostLedger()</c> from the <c>Rag.NET.Storage.Sqlite</c> package,
    /// or a custom store) wins. The in-memory default resets when the process restarts, so
    /// spend limits are only enforced within a single process lifetime — a warning naming
    /// <c>UseSqliteCostLedger()</c> is logged when the default is used. Each registered surface
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
    /// registration to decorate. The ledger path is not part of these options — configure it
    /// via <c>UseSqliteCostLedger(dbPath)</c> from <c>Rag.NET.Storage.Sqlite</c> instead.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a price or limit is negative.</exception>
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
        // TryAdd: an ICostLedger registered earlier (UseSqliteCostLedger from
        // Rag.NET.Storage.Sqlite, or a custom store) wins over the in-memory default. The
        // factory only runs when the default actually won, so the restart-reset warning
        // fires exactly when the in-memory ledger is the one gating the budget.
        builder.Services.TryAddSingleton<ICostLedger>(static sp =>
        {
            sp.GetService<ILogger<InMemoryCostLedger>>()?.LogWarning(
                "UseCostBudgeting is using the default in-memory cost ledger: recorded spend " +
                "resets when the process restarts, so daily/monthly limits are only enforced " +
                "within a single process lifetime. For a ledger that survives restarts, call " +
                "UseSqliteCostLedger() from the Rag.NET.Storage.Sqlite package before " +
                "UseCostBudgeting().");
            return new InMemoryCostLedger(sp.GetService<TimeProvider>());
        });

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
}
