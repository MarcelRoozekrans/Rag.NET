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
    public static TBuilder UseChunkSanitiser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IChunkSanitiser>(sp =>
            new RegexChunkSanitiser(sp.GetService<ILogger<RegexChunkSanitiser>>()));
        return builder;
    }

    public static TBuilder UseLlmChunkSanitiser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IChunkSanitiser>(sp =>
            new LlmChunkSanitiser(
                sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILogger<LlmChunkSanitiser>>()));
        return builder;
    }

    public static TBuilder UseQuerySanitiser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IQuerySanitiser>(sp =>
            new RegexQuerySanitiser(sp.GetService<ILogger<RegexQuerySanitiser>>()));
        EnsureQuerySanitiserDecorator(builder);
        return builder;
    }

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

    public static TBuilder UseRetrievalGuard<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IRetrievalGuard>(sp =>
            new RegexRetrievalGuard(sp.GetService<ILogger<RegexRetrievalGuard>>()));
        return builder;
    }

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
