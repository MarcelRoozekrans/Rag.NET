using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.DependencyInjection;
using Rag.NET.Pipeline;
using Rag.NET.QueryTechniques.ContextualCompression;

namespace Rag.NET.Security;

public static class RagBuilderExtensions
{
    public static TBuilder UseChunkSanitiser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IChunkSanitiser>(sp =>
            new RegexChunkSanitiser(sp.GetRequiredService<ILogger<RegexChunkSanitiser>>()));
        return builder;
    }

    public static TBuilder UseLlmChunkSanitiser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IChunkSanitiser>(sp =>
            new LlmChunkSanitiser(
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<ILogger<LlmChunkSanitiser>>()));
        return builder;
    }

    public static TBuilder UseQuerySanitiser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IQuerySanitiser>(sp =>
            new RegexQuerySanitiser(sp.GetRequiredService<ILogger<RegexQuerySanitiser>>()));
        EnsureQuerySanitiserDecorator(builder);
        return builder;
    }

    public static TBuilder UseLlmQuerySanitiser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IQuerySanitiser>(sp =>
            new LlmQuerySanitiser(
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<ILogger<LlmQuerySanitiser>>()));
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
            new RegexRetrievalGuard(sp.GetRequiredService<ILogger<RegexRetrievalGuard>>()));
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
                sp.GetRequiredService<ILogger<TrustLevelRetrievalGuard>>()));
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
            builder.Services.AddSingleton<ChatAnswerEngine>(sp =>
                new ChatAnswerEngine(
                    sp.GetRequiredService<IChatClient>(),
                    sp.GetService<IConversationMemory>(),
                    sp.GetService<IContextualCompressor>()));
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

    public static TBuilder UseAuditLog<TBuilder>(
        this TBuilder builder, Action<AuditLogOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new AuditLogOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<AuditCorrelationContext>();
        builder.Services.AddSingleton<IAuditLog>(sp =>
            new SqliteAuditLog(
                sp.GetRequiredService<AuditLogOptions>(),
                sp.GetService<ILogger<SqliteAuditLog>>()));

        // Register AuditRetrievalBehavior as a singleton so the pipeline builder can resolve it.
        builder.Services.AddSingleton<AuditRetrievalBehavior>(sp =>
            new AuditRetrievalBehavior(
                sp.GetRequiredService<IAuditLog>(),
                sp.GetService<ICallerContext>() ?? new AnonymousCallerContext(),
                sp.GetRequiredService<AuditLogOptions>(),
                sp.GetService<ILogger<AuditRetrievalBehavior>>(),
                sp.GetService<AuditCorrelationContext>()));

        // Add AuditRetrievalBehavior to the retrieval pipeline via the RetrievalPipelineBuilder in DI.
        // UseAuditLog must be called after AddRagNet — throw clearly if the builder is missing.
        var pipelineBuilder = builder.Services
            .FirstOrDefault(d => d.ServiceType == typeof(RetrievalPipelineBuilder))
            ?.ImplementationInstance as RetrievalPipelineBuilder
            ?? throw new InvalidOperationException(
                "UseAuditLog requires AddRagNet to be called first so that RetrievalPipelineBuilder is registered in DI.");
        pipelineBuilder.AddFirst<AuditRetrievalBehavior>();

        // Wire answer engine decorator — wrap PromptHardeningAnswerEngineDecorator if registered,
        // otherwise fall back to ChatAnswerEngine.
        // NOTE: do NOT resolve via IAnswerEngine here; IAnswerEngine already points to this decorator
        //       and would cause a circular dependency → stack overflow.
        EnsureChatAnswerEngine(builder);
        builder.Services.AddSingleton<AuditAnswerEngineDecorator>(sp =>
            new AuditAnswerEngineDecorator(
                (IAnswerEngine?)sp.GetService<PromptHardeningAnswerEngineDecorator>()
                    ?? sp.GetRequiredService<ChatAnswerEngine>(),
                sp.GetRequiredService<IAuditLog>(),
                sp.GetRequiredService<AuditCorrelationContext>(),
                sp.GetRequiredService<AuditLogOptions>(),
                sp.GetService<ILogger<AuditAnswerEngineDecorator>>()));
        builder.Services.AddSingleton<IAnswerEngine>(sp =>
            sp.GetRequiredService<AuditAnswerEngineDecorator>());

        return builder;
    }

    private static void EnsureChatAnswerEngine<TBuilder>(TBuilder builder)
        where TBuilder : IRagBuilder
    {
        if (!builder.Services.Any(d => d.ServiceType == typeof(ChatAnswerEngine)))
        {
            builder.Services.AddSingleton<ChatAnswerEngine>(sp =>
                new ChatAnswerEngine(
                    sp.GetRequiredService<IChatClient>(),
                    sp.GetService<IConversationMemory>(),
                    sp.GetService<IContextualCompressor>()));
        }
    }
}
