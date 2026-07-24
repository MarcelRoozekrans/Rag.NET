using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.HyDE;
using Rag.NET.QueryTechniques;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseHydeTests
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
    public void UseHyde_RegistersIHypotheticalDocumentGenerator()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseHyde()).BuildServiceProvider();
        Assert.IsType<LlmHypotheticalDocumentGenerator>(sp.GetRequiredService<IHypotheticalDocumentGenerator>());
    }

    [Fact]
    public void UseHyde_DefaultOptions_Registered()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseHyde()).BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<HydeOptions>());
    }

    [Fact]
    public void UseHyde_CustomOptions_Applied()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseHyde(o => o.PromptTemplate = "custom"))
            .BuildServiceProvider();
        Assert.Equal("custom", sp.GetRequiredService<HydeOptions>().PromptTemplate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void UseHyde_InvalidHypothesisCount_Throws(int count)
    {
        var services = BaseServices();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => services.AddRagNet(rag => rag.UseHyde(o => o.HypothesisCount = count)));
    }
}
