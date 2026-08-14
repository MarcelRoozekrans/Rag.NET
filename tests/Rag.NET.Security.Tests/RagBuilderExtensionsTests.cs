using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
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

    /// <summary>
    /// The compliance record must have one entry per retrieval, not one per <c>UseAuditLog</c> call.
    /// <c>AddFirst</c> used to insert unconditionally, so a layered composition root that reached
    /// this method twice put <c>AuditRetrievalBehavior</c> into the pipeline twice and every query
    /// was audited twice — a duplicated audit trail, with nothing about the container looking wrong.
    /// </summary>
    [Fact]
    public async Task UseAuditLog_CalledTwice_AuditsEachRetrievalOnce()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var services = new ServiceCollection();
        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        // UseAuditLog also wraps the answer engine, whose ChatAnswerEngine fallback needs one.
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddRagNet(rag =>
        {
            rag.UseAuditLog();
            rag.UseAuditLog();

            // Registered last so it wins over the SqliteAuditLog the extension registers — the
            // behaviour resolves IAuditLog, so this is what it will write through.
            rag.Services.AddSingleton(auditLog);
        });

        var sp = services.BuildServiceProvider();
        _ = await sp.GetRequiredService<IRagPipeline>()
            .RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        await auditLog.Received(1).LogRetrievalAsync(Arg.Any<AuditRetrievalEvent>(), Arg.Any<CancellationToken>());
    }
}
