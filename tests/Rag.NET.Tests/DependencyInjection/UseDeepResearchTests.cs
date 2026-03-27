using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseDeepResearchTests
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
    public void UseDeepResearch_IRetrieverIsDeepResearchRetriever()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseDeepResearch()).BuildServiceProvider();
        Assert.IsType<DeepResearchRetriever>(sp.GetRequiredService<IRetriever>());
    }

    [Fact]
    public void UseDeepResearch_DefaultOptions_MaxDepthIsThree()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseDeepResearch()).BuildServiceProvider();
        Assert.Equal(3, sp.GetRequiredService<DeepResearchOptions>().MaxDepth);
    }

    [Fact]
    public void UseDeepResearch_CustomOptions_Registered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseDeepResearch(new DeepResearchOptions { MaxDepth = 1 }))
            .BuildServiceProvider();
        Assert.Equal(1, sp.GetRequiredService<DeepResearchOptions>().MaxDepth);
    }

    [Fact]
    public void WithoutUseDeepResearch_IRetrieverIsPipelineRetriever()
    {
        var sp = BaseServices().AddRagNet().BuildServiceProvider();
        Assert.IsType<PipelineRetriever>(sp.GetRequiredService<IRetriever>());
    }
}
