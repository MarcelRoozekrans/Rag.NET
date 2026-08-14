using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Resilience;
using Polly;
using Polly.Registry;
using Polly.Retry;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Resilience;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Resilience registration extensions for the RAG builder. These methods (and the decorators
/// they register) lived in the core <c>Rag.NET</c> package before the package decomposition;
/// they keep their namespaces so existing consumer source compiles unchanged after adding a
/// reference to <c>Rag.NET.Resilience</c>.
/// </summary>
public static class ResilienceBuilderExtensions
{
    /// <summary>Name of the Polly resilience pipeline registered by <see cref="ConfigureResilience"/>.</summary>
    internal const string ResiliencePipelineName = "rag-net";

    /// <summary>Sentinel service key marking that <see cref="ConfigureResilience"/> has decorated the surfaces.</summary>
    internal const string ResilienceAppliedKey = "ragnet.resilience.applied";

    /// <summary>Service key of the chat-surface <see cref="IRateLimiter"/> registered by <c>UseRateLimiting</c>.</summary>
    internal const string ChatRateLimiterKey = "ragnet.ratelimit.chat";

    /// <summary>Service key of the embedding-surface <see cref="IRateLimiter"/> registered by <c>UseRateLimiting</c>.</summary>
    internal const string EmbeddingRateLimiterKey = "ragnet.ratelimit.embedding";

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
    /// A surface registered <b>afterwards</b> is not retried, and since issue #195 not quietly:
    /// resolving the RAG pipeline throws an <see cref="InvalidOperationException"/> naming this
    /// method and the surface it missed. Finding one of the two surfaces used to satisfy the
    /// "nothing to apply to" check below and skip the other in silence.
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
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">
    /// Optional delegate to customise the <see cref="ResiliencePipelineBuilder"/>.
    /// Replaces the default retry policy when supplied.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when neither an <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> nor an
    /// <see cref="IVectorStore"/> is registered yet — there would be nothing to apply the pipeline to.
    /// </exception>
    public static TBuilder ConfigureResilience<TBuilder>(
        this TBuilder builder,
        Action<ResiliencePipelineBuilder>? configure = null)
        where TBuilder : IRagBuilder
    {
        // Idempotence: a repeat call must not stack a second decorator layer (first-wins,
        // mirroring UseRateLimiting/UseCostBudgeting). The named pipeline is still
        // re-registered so the latest configuration wins for the already-wired decorators.
        bool alreadyApplied = builder.Services.Any(static d =>
            d.IsKeyedService && Equals(d.ServiceKey, ResilienceAppliedKey));

        bool decorateEmbedding = !alreadyApplied
            && ServiceDecorationHelper.IsRegistered<IEmbeddingGenerator<string, Embedding<float>>>(builder.Services);
        bool decorateStore = !alreadyApplied && ServiceDecorationHelper.IsRegistered<IVectorStore>(builder.Services);

        // Validate before mutating the collection, so a misordered call leaves no half-applied state.
        if (!alreadyApplied && !decorateEmbedding && !decorateStore)
        {
            throw new InvalidOperationException(
                "ConfigureResilience found no IEmbeddingGenerator or IVectorStore registration to apply " +
                "the \"rag-net\" pipeline to. Register the vector store and/or embedding generator " +
                "(UsePgVector, UseQdrant, your embedding provider, …) before calling ConfigureResilience — " +
                "it wraps whatever is registered at that point.");
        }

        builder.Services.AddResiliencePipeline(ResiliencePipelineName, b => BuildPipeline(b, configure));

        if (alreadyApplied)
        {
            return builder;
        }

        builder.Services.AddKeyedSingleton<object>(ResilienceAppliedKey, static (_, _) => new object());

        // Both surfaces are claimed even when only one is registered: finding one of the two used to
        // satisfy the guard above and skip the other in silence (issue #195).
        var embeddingProbe = CompositionClaims.Claim<IEmbeddingGenerator<string, Embedding<float>>>(
            builder.Services, nameof(ConfigureResilience));
        var storeProbe = CompositionClaims.Claim<IVectorStore>(builder.Services, nameof(ConfigureResilience));

        if (decorateEmbedding)
        {
            ServiceDecorationHelper.Decorate<IEmbeddingGenerator<string, Embedding<float>>>(
                builder.Services, (inner, sp) =>
                {
                    embeddingProbe.Applied = true;
                    return new ResilientEmbeddingGenerator(inner, ResolvePipeline(sp));
                });
        }

        if (decorateStore)
        {
            ServiceDecorationHelper.Decorate<IVectorStore>(
                builder.Services, (inner, sp) =>
                {
                    storeProbe.Applied = true;
                    return ResilientVectorStore.Create(inner, ResolvePipeline(sp));
                });
        }

        return builder;
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
    /// Registers a <see cref="FallbackChatClient"/> as the <see cref="IChatClient"/>:
    /// calls try the configured clients in order, falling through to the next on transient
    /// failures (HTTP 429/503, timeouts, rate-limit/unavailable error text) and — when
    /// <see cref="FallbackChainOptions.PerClientTimeout"/> is set — when a client exceeds
    /// the per-attempt timeout. Non-transient errors propagate immediately.
    /// </summary>
    /// <remarks>
    /// This registration supersedes any prior <see cref="IChatClient"/> registration
    /// (standard last-wins container semantics, same convention as
    /// <c>UseFederatedSearch</c>). That is the supported direction and only that one: an
    /// <see cref="IChatClient"/> registered <b>after</b> this call replaces the chain, so
    /// resolving the RAG pipeline throws an <see cref="InvalidOperationException"/> rather than
    /// leaving failover configured and unreachable (issue #195). Add every provider client to the
    /// chain through <see cref="FallbackChainOptions.AddClient"/> instead of registering one
    /// separately. Clients are configured as factories so each
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

        // The chain superseding a prior IChatClient is the documented direction; the reverse is a
        // fallback chain that was configured, validated and unreachable, which is only ever found
        // during the outage it was configured for (issue #195).
        var probe = CompositionClaims.Claim<IChatClient>(builder.Services, nameof(UseFallbackChain));

        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            probe.Applied = true;
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
    /// at registration time, before either surface is decorated (no half-applied state), and a
    /// registration that <b>replaces</b> a decorated surface afterwards fails when the RAG
    /// pipeline is resolved (issue #195) rather than dropping the limiter in silence.
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
            var probe = CompositionClaims.Claim<IChatClient>(builder.Services, nameof(UseRateLimiting));
            builder.Services.AddKeyedSingleton<IRateLimiter>(ChatRateLimiterKey,
                (_, _) => new TokenBucketRateLimiterAdapter(chatRpm, "chat", maxQueuedRequests));
            ServiceDecorationHelper.Decorate<IChatClient>(builder.Services, (inner, sp) =>
            {
                probe.Applied = true;
                return new RateLimitedChatClient(inner, sp.GetRequiredKeyedService<IRateLimiter>(ChatRateLimiterKey));
            });
        }

        if (limitEmbedding)
        {
            int embeddingRpm = options.EmbeddingRequestsPerMinute!.Value;
            var probe = CompositionClaims.Claim<IEmbeddingGenerator<string, Embedding<float>>>(
                builder.Services, nameof(UseRateLimiting));
            builder.Services.AddKeyedSingleton<IRateLimiter>(EmbeddingRateLimiterKey,
                (_, _) => new TokenBucketRateLimiterAdapter(embeddingRpm, "embedding", maxQueuedRequests));
            ServiceDecorationHelper.Decorate<IEmbeddingGenerator<string, Embedding<float>>>(builder.Services, (inner, sp) =>
            {
                probe.Applied = true;
                return new RateLimitedEmbeddingGenerator(inner, sp.GetRequiredKeyedService<IRateLimiter>(EmbeddingRateLimiterKey));
            });
        }

        return builder;
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
