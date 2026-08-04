using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// Covers <see cref="CachingBuilderExtensions.UseCaching"/> switching core's always-composed
/// cache behaviours from no-op to caching, end to end through the real pipeline and the real
/// <c>HybridCache</c> implementation. Both tests lived in core's
/// <c>ServiceCollectionExtensionsTests</c> before the package decomposition and moved here with
/// the extension, unchanged.
/// </summary>
public class UseCachingTests
{
    [Fact]
    public async Task AddRagNet_WithCaching_CachesSecondCall()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        services.AddRagNet(b => b.UseCaching());

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        _ = await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);
        _ = await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        // Second call should be cached — embedder called only once
        await embedder.Received(1).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRagNet_WithoutCaching_DoesNotCacheSecondCall()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        services.AddRagNet(); // no UseCaching()

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        _ = await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);
        _ = await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        // Without caching, embedder should be called twice
        await embedder.Received(2).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }
}
