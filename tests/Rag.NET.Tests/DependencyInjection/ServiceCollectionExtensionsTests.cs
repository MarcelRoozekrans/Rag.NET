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

    [Fact]
    public async Task AddRagNet_WithMultiQuery_ChainsMultiQueryRetriever()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var queryExpander = Substitute.For<IQueryExpander>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        services.AddSingleton(queryExpander);
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
        queryExpander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "variant 1" });

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("query", new RetrievalOptions { UseMultiQuery = true }, TestContext.Current.CancellationToken);

        await queryExpander.Received(1).ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRagNet_WithHyde_ChainsHydeRetriever()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var hydeGenerator = Substitute.For<IHypotheticalDocumentGenerator>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        services.AddSingleton(hydeGenerator);

        hydeGenerator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("hypothetical document");
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        await hydeGenerator.Received(1).GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRagNet_WithAllFeatures_ChainsFullDecoratorPipeline()
    {
        var services = new ServiceCollection();
        var vectorStore = Substitute.For<IVectorStore>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var reranker = Substitute.For<IReranker>();
        var queryExpander = Substitute.For<IQueryExpander>();
        var hydeGenerator = Substitute.For<IHypotheticalDocumentGenerator>();

        services.AddSingleton(vectorStore);
        services.AddSingleton(embedder);
        services.AddSingleton(reranker);
        services.AddSingleton(queryExpander);
        services.AddSingleton(hydeGenerator);

        hydeGenerator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("hypothetical document");
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
        queryExpander.ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "variant" });
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .Returns(new List<RerankResult>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        var opts = new RetrievalOptions
        {
            UseMultiQuery = true,
            UseReranking = true,
            UseRedundancyFilter = true,
            UseLostInTheMiddleReordering = true,
            UseHyde = true,
        };

        await pipeline.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        await hydeGenerator.Received().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await queryExpander.Received(1).ExpandAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await reranker.Received(1).RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
    }

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

        await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);
        await pipeline.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        // Second call should be cached — embedder called only once
        await embedder.Received(1).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AddRagNet_WithoutOptionalDeps_ResolvesPipeline()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        services.AddRagNet();

        var sp = services.BuildServiceProvider();
        var retriever = sp.GetRequiredService<IRetriever>();
        var ingestor = sp.GetRequiredService<IIngestor>();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        Assert.NotNull(retriever);
        Assert.NotNull(ingestor);
        Assert.NotNull(pipeline);
    }
}
