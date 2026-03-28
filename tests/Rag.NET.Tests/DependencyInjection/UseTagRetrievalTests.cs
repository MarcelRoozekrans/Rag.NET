using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseTagRetrievalTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IChatClient>());
        return services;
    }

    [Fact]
    public void UseTagRetrieval_IRetrieverIsTagRetriever()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseTagRetrieval()).BuildServiceProvider();
        Assert.IsType<TagRetriever>(sp.GetRequiredService<IRetriever>());
    }

    [Fact]
    public void UseTagRetrieval_ITagIndexIsInMemoryTagIndex()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseTagRetrieval()).BuildServiceProvider();
        Assert.IsType<InMemoryTagIndex>(sp.GetRequiredService<ITagIndex>());
    }

    [Fact]
    public void UseTagRetrieval_DefaultOptions_TopKIsOne()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseTagRetrieval()).BuildServiceProvider();
        Assert.Equal(1, sp.GetRequiredService<TagRetrievalOptions>().TopK);
    }

    [Fact]
    public void UseTagRetrieval_CustomOptions_Registered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseTagRetrieval(new TagRetrievalOptions { TopK = 3 }))
            .BuildServiceProvider();
        Assert.Equal(3, sp.GetRequiredService<TagRetrievalOptions>().TopK);
    }

    [Fact]
    public void WithoutUseTagRetrieval_IRetrieverIsPipelineRetriever()
    {
        var sp = BaseServices().AddRagNet().BuildServiceProvider();
        Assert.IsType<PipelineRetriever>(sp.GetRequiredService<IRetriever>());
    }

    [Fact]
    public void UseTagRetrieval_And_UseDeepResearch_TagRetrieverWrapsDeepResearch()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseDeepResearch().UseTagRetrieval())
            .BuildServiceProvider();

        // TagRetriever is the outermost (IRetriever)
        Assert.IsType<TagRetriever>(sp.GetRequiredService<IRetriever>());
        // DeepResearchRetriever is registered as concrete
        Assert.IsType<DeepResearchRetriever>(sp.GetRequiredService<DeepResearchRetriever>());
    }

    [Fact]
    public async Task UseTagRetrieval_RetrieveAsync_InvokesEmbedder()
    {
        var ct      = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
                .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0.1f })]));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
                   .Returns(new List<SearchResult>());

        var services = new ServiceCollection();
        services.AddSingleton(embedder);
        services.AddSingleton(vectorStore);
        services.AddSingleton(Substitute.For<IChatClient>());
        var sp = services.AddRagNet(rag => rag.UseTagRetrieval()).BuildServiceProvider();

        var retriever = sp.GetRequiredService<IRetriever>();
        _ = await retriever.RetrieveAsync("what is the budget?", null, ct);

        // TagRetriever must have called the embedder for tag lookup (may be called again by the inner pipeline)
        await embedder.Received()
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }
}
