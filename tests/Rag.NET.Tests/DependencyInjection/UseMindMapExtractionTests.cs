using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.GraphRag;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseMindMapExtractionTests
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
    public void UseMindMapExtraction_RegistersMindMapExtractor()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMindMapExtraction())
            .BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<MindMapExtractor>());
    }

    [Fact]
    public void UseMindMapExtraction_RegistersMindMapExtractionBehavior()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMindMapExtraction())
            .BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<MindMapExtractionBehavior>());
    }

    [Fact]
    public void UseMindMapExtraction_DefaultOptions_ExtractAtIngestionIsFalse()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMindMapExtraction())
            .BuildServiceProvider();

        Assert.False(sp.GetRequiredService<MindMapOptions>().ExtractAtIngestion);
    }

    [Fact]
    public void UseMindMapExtraction_CustomOptions_Registered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMindMapExtraction(o =>
            {
                o.ExtractAtIngestion = true;
                o.MaxDepth = 5;
            }))
            .BuildServiceProvider();

        var opts = sp.GetRequiredService<MindMapOptions>();
        Assert.True(opts.ExtractAtIngestion);
        Assert.Equal(5, opts.MaxDepth);
    }

    [Fact]
    public void UseMindMapExtraction_WithoutGraphRag_DoesNotThrow()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseMindMapExtraction())
            .BuildServiceProvider();

        var extractor = sp.GetRequiredService<MindMapExtractor>();
        Assert.NotNull(extractor);
    }
}
