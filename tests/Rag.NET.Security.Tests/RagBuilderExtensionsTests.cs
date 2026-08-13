using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class RagBuilderExtensionsTests
{
    /// <summary>
    /// The minimum a user actually registers — vector store, embedding generator, chat client —
    /// with no logging at all. A missing logger must degrade to no logging, never to a crash.
    /// </summary>
    private static IServiceCollection ServicesWithoutLogging()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    [Fact]
    public void UseChunkSanitiser_WithoutLoggingRegistered_Resolves()
    {
        var sp = ServicesWithoutLogging()
            .AddRagNet(rag => rag.UseChunkSanitiser()).BuildServiceProvider();
        Assert.IsType<RegexChunkSanitiser>(sp.GetRequiredService<IChunkSanitiser>());
    }

    [Fact]
    public void UseLlmChunkSanitiser_WithoutLoggingRegistered_Resolves()
    {
        var sp = ServicesWithoutLogging()
            .AddRagNet(rag => rag.UseLlmChunkSanitiser()).BuildServiceProvider();
        Assert.IsType<LlmChunkSanitiser>(sp.GetRequiredService<IChunkSanitiser>());
    }

    [Fact]
    public void UseQuerySanitiser_WithoutLoggingRegistered_Resolves()
    {
        var sp = ServicesWithoutLogging()
            .AddRagNet(rag => rag.UseQuerySanitiser()).BuildServiceProvider();
        Assert.IsType<RegexQuerySanitiser>(sp.GetRequiredService<IQuerySanitiser>());
    }

    [Fact]
    public void UseLlmQuerySanitiser_WithoutLoggingRegistered_Resolves()
    {
        var sp = ServicesWithoutLogging()
            .AddRagNet(rag => rag.UseLlmQuerySanitiser()).BuildServiceProvider();
        Assert.IsType<LlmQuerySanitiser>(sp.GetRequiredService<IQuerySanitiser>());
    }

    [Fact]
    public void UseRetrievalGuard_WithoutLoggingRegistered_Resolves()
    {
        var sp = ServicesWithoutLogging()
            .AddRagNet(rag => rag.UseRetrievalGuard()).BuildServiceProvider();
        Assert.IsType<RegexRetrievalGuard>(sp.GetRequiredService<IRetrievalGuard>());
    }

    [Fact]
    public void UseTrustLevelGuard_WithoutLoggingRegistered_Resolves()
    {
        var sp = ServicesWithoutLogging()
            .AddRagNet(rag => rag.UseTrustLevelGuard()).BuildServiceProvider();
        Assert.IsType<TrustLevelRetrievalGuard>(sp.GetRequiredService<IRetrievalGuard>());
    }

    [Fact]
    public void UseAuditLog_WithoutAddRagNetFirst_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        // Create a RagBuilder manually without calling AddRagNet (so no RetrievalPipelineBuilder registered)
        var builder = new RagBuilder(services);

        var ex = Assert.Throws<InvalidOperationException>(() => builder.UseAuditLog());
        Assert.Contains("AddRagNet", ex.Message, StringComparison.Ordinal);
    }
}
