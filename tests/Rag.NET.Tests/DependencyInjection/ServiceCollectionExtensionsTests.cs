using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRagNet_RegistersIRagPipeline()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IRagPipeline>());
    }

    [Fact]
    public void AddRagNet_RegistersIRetriever()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IRetriever>());
    }

    [Fact]
    public void AddRagNet_RegistersIIngestor()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IIngestor>());
    }

    [Fact]
    public async Task AddRagNet_WithReranking_ChainsRerankingRetriever()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var reranker = Substitute.For<IReranker>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .Returns(new List<RerankResult>());

        services.AddRagNet();
        services.AddSingleton(reranker);

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("query", new RetrievalOptions { UseReranking = true }, TestContext.Current.CancellationToken);

        await reranker.Received(1).RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
    }

    private class FakeReranker : IReranker
    {
        public Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<SearchResult> results, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RerankResult>>(new List<RerankResult>());
    }
}
