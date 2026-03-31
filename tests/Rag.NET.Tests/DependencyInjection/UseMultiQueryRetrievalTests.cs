using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Rag.NET.QueryTechniques;
using Rag.NET.MultiQuery;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseMultiQueryRetrievalTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    [Fact]
    public void UseMultiQueryRetrieval_RegistersIQueryExpander()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseMultiQueryRetrieval()).BuildServiceProvider();
        Assert.IsType<LlmQueryExpander>(sp.GetRequiredService<IQueryExpander>());
    }

    [Fact]
    public void UseMultiQueryRetrieval_DefaultOptions_Registered()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseMultiQueryRetrieval()).BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<MultiQueryOptions>());
    }

    [Fact]
    public void UseMultiQueryRetrieval_CustomOptions_Applied()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMultiQueryRetrieval(o => o.VariantCount = 5))
            .BuildServiceProvider();
        Assert.Equal(5, sp.GetRequiredService<MultiQueryOptions>().VariantCount);
    }
}
