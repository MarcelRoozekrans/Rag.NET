using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
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
}
