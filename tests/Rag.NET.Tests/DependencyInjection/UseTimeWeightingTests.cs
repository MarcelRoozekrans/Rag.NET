using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseTimeWeightingTests
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
    public void UseTimeWeighting_IRetrieverIsTimeWeightedRetriever()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseTimeWeighting()).BuildServiceProvider();
        Assert.IsType<TimeWeightedRetriever>(sp.GetRequiredService<IRetriever>());
    }

    [Fact]
    public void UseTimeWeighting_DefaultOptions_DecayRateIs001()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseTimeWeighting()).BuildServiceProvider();
        Assert.Equal(0.01, sp.GetRequiredService<TimeWeightedOptions>().DecayRate);
    }

    [Fact]
    public void UseTimeWeighting_CustomOptions_Registered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseTimeWeighting(new TimeWeightedOptions { DecayRate = 0.005 }))
            .BuildServiceProvider();
        Assert.Equal(0.005, sp.GetRequiredService<TimeWeightedOptions>().DecayRate);
    }

    [Fact]
    public void WithoutUseTimeWeighting_IRetrieverIsPipelineRetriever()
    {
        var sp = BaseServices().AddRagNet().BuildServiceProvider();
        Assert.IsType<PipelineRetriever>(sp.GetRequiredService<IRetriever>());
    }

    [Fact]
    public void UseTimeWeighting_And_UseTagRetrieval_TagRetrieverWrapsTimeWeighted()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseTimeWeighting().UseTagRetrieval())
            .BuildServiceProvider();

        // TagRetriever is outermost
        Assert.IsType<TagRetriever>(sp.GetRequiredService<IRetriever>());
        // TimeWeightedRetriever is registered as concrete
        Assert.IsType<TimeWeightedRetriever>(sp.GetRequiredService<TimeWeightedRetriever>());
    }

    [Fact]
    public void UseTimeWeighting_And_UseDeepResearch_TimeWeightedWrapsDeepResearch()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseDeepResearch().UseTimeWeighting())
            .BuildServiceProvider();

        // TimeWeightedRetriever is outermost
        Assert.IsType<TimeWeightedRetriever>(sp.GetRequiredService<IRetriever>());
        // DeepResearchRetriever is registered as concrete
        Assert.IsType<DeepResearchRetriever>(sp.GetRequiredService<DeepResearchRetriever>());
    }

    [Fact]
    public void UseTimeWeighting_And_UseDeepResearch_And_UseTagRetrieval_FullStack()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseDeepResearch().UseTimeWeighting().UseTagRetrieval())
            .BuildServiceProvider();

        // TagRetriever is outermost
        Assert.IsType<TagRetriever>(sp.GetRequiredService<IRetriever>());
        // Both inner decorators registered as concrete types
        Assert.IsType<TimeWeightedRetriever>(sp.GetRequiredService<TimeWeightedRetriever>());
        Assert.IsType<DeepResearchRetriever>(sp.GetRequiredService<DeepResearchRetriever>());
    }
}
