using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.DependencyInjection;
using Rag.NET.Pipeline;

namespace Rag.NET.Security;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Strips known prompt-injection patterns from chunk text at ingest, before embedding, so
    /// attacker-controlled content cannot carry instructions into the vector store.
    /// </summary>
    /// <remarks>
    /// Registers a regex <see cref="IChunkSanitiser"/> into the <b>same ordered chain</b>
    /// <c>UsePiiDetection</c> uses — sanitisers run in registration order and each sees the
    /// previous one's output. Matches become <c>[REDACTED]</c> and are logged. <b>Fails open</b>:
    /// if sanitisation throws, the original text is returned unchanged and the failure is logged.
    /// </remarks>
    public static TBuilder UseChunkSanitiser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IChunkSanitiser>(sp =>
            new RegexChunkSanitiser(sp.GetService<ILogger<RegexChunkSanitiser>>()));
        return builder;
    }

    /// <summary>
    /// The semantic counterpart to <see cref="UseChunkSanitiser{TBuilder}"/>: asks a model whether
    /// chunk text contains injection attempts, catching paraphrase the regex cannot.
    /// </summary>
    /// <remarks>
    /// Costs a model call per chunk at ingest. Resolves <c>IChatClient</c> with
    /// <c>GetRequiredService</c>, so registration <b>throws at container resolution</b> when no
    /// chat client is registered. Chains with the regex sanitiser rather than replacing it.
    /// </remarks>
    public static TBuilder UseLlmChunkSanitiser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IChunkSanitiser>(sp =>
            new LlmChunkSanitiser(
                sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILogger<LlmChunkSanitiser>>()));
        return builder;
    }

    /// <summary>
    /// Strips known prompt-injection patterns from the user's query before retrieval.
    /// </summary>
    /// <remarks>
    /// <b>Applies to <c>AskAsync</c> and <c>AskStreamingAsync</c> only.</b>
    /// <c>RetrieveAsync</c> forwards the query unchanged, so a retrieval-only caller gets no query
    /// sanitisation on that path. <b>Fails open</b>: a sanitiser that throws returns the original
    /// query and logs.
    /// </remarks>
    public static TBuilder UseQuerySanitiser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IQuerySanitiser>(sp =>
            new RegexQuerySanitiser(sp.GetService<ILogger<RegexQuerySanitiser>>()));
        EnsureQuerySanitiserDecorator(builder);
        return builder;
    }

    /// <summary>
    /// The semantic counterpart to <see cref="UseQuerySanitiser{TBuilder}"/>, catching paraphrased
    /// injection attempts in the query that the regex pattern misses.
    /// </summary>
    /// <remarks>
    /// Costs a model call per query, and carries the same
    /// <c>AskAsync</c>/<c>AskStreamingAsync</c>-only scope as the regex variant. Resolves
    /// <c>IChatClient</c> with <c>GetRequiredService</c>, so registration <b>throws at container
    /// resolution</b> when none is registered.
    /// </remarks>
    public static TBuilder UseLlmQuerySanitiser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IQuerySanitiser>(sp =>
            new LlmQuerySanitiser(
                sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILogger<LlmQuerySanitiser>>()));
        EnsureQuerySanitiserDecorator(builder);
        return builder;
    }

    private static void EnsureQuerySanitiserDecorator<TBuilder>(TBuilder builder)
        where TBuilder : IRagBuilder
    {
        if (builder.Services.Any(d => d.ServiceType == typeof(QuerySanitiserPipelineDecorator)))
            return;
        builder.Services.AddSingleton<QuerySanitiserPipelineDecorator>(sp =>
            new QuerySanitiserPipelineDecorator(
                sp.GetRequiredService<RagPipeline>(),
                sp.GetServices<IQuerySanitiser>()));
        builder.Services.AddSingleton<IRagPipeline>(sp =>
            sp.GetRequiredService<QuerySanitiserPipelineDecorator>());
    }

    /// <summary>
    /// Redacts injection patterns from retrieved chunk text, after retrieval and before the chunks
    /// reach the answer prompt — the layer that catches what survived ingest-time sanitisation.
    /// </summary>
    /// <remarks>
    /// Registers an <see cref="IRetrievalGuard"/>, the <b>same extension point
    /// <c>UseRbac</c> uses</b>, so guards compose by registration order. Emits a
    /// <c>ragnet.security.guard</c> activity tagged <c>action=redact</c>, which is how a caller
    /// confirms it ran.
    /// </remarks>
    public static TBuilder UseRetrievalGuard<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IRetrievalGuard>(sp =>
            new RegexRetrievalGuard(sp.GetService<ILogger<RegexRetrievalGuard>>()));
        return builder;
    }

    /// <summary>
    /// Drops or flags retrieved chunks by their <c>trust_level</c> metadata, so content pulled from
    /// adversarial sources can be excluded from the answer prompt.
    /// </summary>
    /// <remarks>
    /// <b>A chunk with no <c>trust_level</c> key is treated as <c>internal</c></b> — the most
    /// trusted value — so this guard drops nothing over a corpus ingested without trust tagging.
    /// That is deliberate: denying untagged content would hide an entire existing corpus the moment
    /// the guard is registered. Set the level at ingest, on whatever pulls the content. Emits a
    /// <c>ragnet.security.guard</c> activity tagged <c>action=drop</c>.
    /// </remarks>
    /// <param name="builder">The builder to register into.</param>
    /// <param name="configure">
    /// Adjusts <see cref="TrustLevelGuardOptions"/>; defaults drop <c>untrusted</c> and warn on
    /// <c>external</c>.
    /// </param>
    public static TBuilder UseTrustLevelGuard<TBuilder>(
        this TBuilder builder, Action<TrustLevelGuardOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new TrustLevelGuardOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<IRetrievalGuard>(sp =>
            new TrustLevelRetrievalGuard(
                sp.GetRequiredService<TrustLevelGuardOptions>(),
                sp.GetService<ILogger<TrustLevelRetrievalGuard>>()));
        return builder;
    }

    /// <summary>
    /// Prefixes the system prompt with an instruction to treat all retrieved content as data and
    /// never as instructions — the layer that assumes the others leaked.
    /// </summary>
    /// <remarks>
    /// Decorates the answer engine, costs nothing per request, and is the cheapest of the four
    /// injection defences. Replace the wording through
    /// <see cref="PromptHardeningOptions.SystemPrefix"/>.
    /// </remarks>
    /// <param name="builder">The builder to register into.</param>
    /// <param name="configure">Adjusts <see cref="PromptHardeningOptions"/>.</param>
    public static TBuilder UsePromptHardening<TBuilder>(
        this TBuilder builder, Action<PromptHardeningOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new PromptHardeningOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);

        // Register ChatAnswerEngine as its concrete type so the decorator can wrap it.
        // Same pattern as UseQuerySanitiser registering RagPipeline by concrete type.
        if (!builder.Services.Any(d => d.ServiceType == typeof(ChatAnswerEngine)))
        {
            builder.Services.AddSingleton(ChatAnswerEngine.CreateFromServices);
        }

        // Register decorator as concrete type, then replace IAnswerEngine with it.
        builder.Services.AddSingleton<PromptHardeningAnswerEngineDecorator>(sp =>
            new PromptHardeningAnswerEngineDecorator(
                sp.GetRequiredService<ChatAnswerEngine>(),
                sp.GetRequiredService<PromptHardeningOptions>()));
        builder.Services.AddSingleton<IAnswerEngine>(sp =>
            sp.GetRequiredService<PromptHardeningAnswerEngineDecorator>());

        return builder;
    }

    /// <summary>
    /// Filters retrieved chunks to those the current caller may see, by matching their roles
    /// against each chunk's <c>allowed_roles</c> metadata.
    /// </summary>
    /// <remarks>
    /// <b>Chunks without the key are world-readable</b> and pass through for every caller — this is
    /// not deny-by-default, and registering it over an untagged corpus filters nothing. Requires an
    /// <c>ICallerContext</c>; registers an <see cref="IRetrievalGuard"/>, the same extension point
    /// the injection guards use.
    /// </remarks>
    public static TBuilder UseRbac<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        // ICallerContext must be registered separately (e.g. via AddRagNetAspNetCoreSecurity)
        builder.Services.AddSingleton<IRetrievalGuard>(sp =>
            new RbacRetrievalGuard(
                sp.GetRequiredService<ICallerContext>(),
                sp.GetService<ILogger<RbacRetrievalGuard>>()));
        return builder;
    }

    /// <summary>
    /// Redacts personal data from chunk text at ingest, before embeddings are stored, so PII never
    /// reaches the vector store.
    /// </summary>
    /// <remarks>
    /// Registers a regex <see cref="IChunkSanitiser"/> into the <b>same ordered chain</b> the
    /// injection sanitisers use. Each pattern is evaluated with a one-second timeout; a timeout
    /// logs and returns the text unchanged rather than blocking ingestion.
    /// </remarks>
    public static TBuilder UsePiiDetection<TBuilder>(
        this TBuilder builder, Action<PiiDetectionOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new PiiDetectionOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<IChunkSanitiser>(sp =>
            new PiiChunkSanitiser(
                sp.GetRequiredService<PiiDetectionOptions>(),
                sp.GetService<ILogger<PiiChunkSanitiser>>()));
        return builder;
    }

    /// <summary>
    /// The semantic counterpart to <see cref="UsePiiDetection{TBuilder}"/>, catching personal data
    /// the regex patterns do not describe.
    /// </summary>
    /// <remarks>
    /// Costs a model call per chunk. Chains after the regex detector when both are registered, so
    /// the model sees already-redacted text — lower cost and less to hallucinate. Resolves
    /// <c>IChatClient</c> with <c>GetRequiredService</c>, so registration <b>throws at container
    /// resolution</b> when none is registered.
    /// </remarks>
    public static TBuilder UseLlmPiiDetection<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IChunkSanitiser>(sp =>
            new LlmPiiChunkSanitiser(
                sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILogger<LlmPiiChunkSanitiser>>()));
        return builder;
    }

    /// <summary>
    /// Registers everything auditing needs <b>except</b> the <see cref="IAuditLog"/> itself: the
    /// options, the correlation context, the retrieval behaviour and the answer-engine decorator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Internal on purpose.</b> There used to be a public <c>UseAuditLog()</c> here that also
    /// registered <see cref="SqliteAuditLog"/>, which is why <c>Rag.NET.Security</c> carried
    /// <c>Microsoft.Data.Sqlite</c> and a native SQLite binary for everyone using
    /// <c>UseChunkSanitiser</c>, <c>UseRbac</c> or <c>UsePiiDetection</c> (#339).
    /// </para>
    /// <para>
    /// It is not public because a public version would let a caller register the behaviour and the
    /// decorator with no log behind them — auditing that appears configured and records nothing.
    /// Keeping the wiring internal makes that state <b>unrepresentable</b> rather than merely
    /// detectable: the only way to reach it is through a package that also supplies a log, so a
    /// caller who forgets gets a compile error rather than a silent gap or a runtime surprise.
    /// </para>
    /// </remarks>
    /// <typeparam name="TBuilder">The builder being configured.</typeparam>
    /// <param name="builder">The builder being configured.</param>
    /// <param name="opts">
    /// The audit options, already configured by the caller and about to be shared with the
    /// <see cref="IAuditLog"/> that caller registers.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    internal static TBuilder AddAuditWiring<TBuilder>(this TBuilder builder, AuditLogOptions opts)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<AuditCorrelationContext>();
        // Register AuditRetrievalBehavior as a singleton so the pipeline builder can resolve it.
        builder.Services.AddSingleton<AuditRetrievalBehavior>(sp =>
            new AuditRetrievalBehavior(
                sp.GetRequiredService<IAuditLog>(),
                sp.GetService<ICallerContext>() ?? new AnonymousCallerContext(),
                sp.GetRequiredService<AuditLogOptions>(),
                sp.GetService<ILogger<AuditRetrievalBehavior>>(),
                sp.GetService<AuditCorrelationContext>(),
                // GetService, not GetRequiredService: nothing registers one in production, so the
                // default stands and behaviour is unchanged. A test registers its own (#380).
                sp.GetService<IGuidProvider>()));

        // Add AuditRetrievalBehavior to the retrieval pipeline via the RetrievalPipelineBuilder in DI.
        // The caller's Use* must run after AddRagNet — the accessor throws clearly if it was not.
        builder.Services.RagRetrievalPipeline(nameof(AddAuditWiring)).AddFirst<AuditRetrievalBehavior>();

        // Wire the answer-engine decorator through the decoration seam rather than by registering
        // IAnswerEngine. Registering it is how an answer engine is *chosen* (UseMapReduceAnswerEngine,
        // UseFlare, UsePromptHardening, …), so doing both through one registration made the two
        // cancel each other out on last-wins and dropped whichever ran first — with retrieval
        // auditing still working, so the audit log read as complete and recorded no answers at all
        // (issue #195). Through the seam the decorator wraps whatever engine the pipeline composes,
        // in either order, and there is no circular resolution to avoid: the inner engine is handed
        // in rather than resolved.
        builder.Services.RagAnswerEngineDecorations(nameof(AddAuditWiring)).Add(
            nameof(AddAuditWiring),
            static (inner, sp) => new AuditAnswerEngineDecorator(
                inner,
                sp.GetRequiredService<IAuditLog>(),
                sp.GetRequiredService<AuditCorrelationContext>(),
                sp.GetRequiredService<AuditLogOptions>(),
                sp.GetService<ILogger<AuditAnswerEngineDecorator>>(),
                sp.GetService<IGuidProvider>()));

        return builder;
    }
}
