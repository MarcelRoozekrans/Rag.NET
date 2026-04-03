using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Pipeline;

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
        return builder;
    }
}
