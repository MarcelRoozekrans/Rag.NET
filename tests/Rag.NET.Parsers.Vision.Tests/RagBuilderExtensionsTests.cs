using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Parsers.Vision;
using Xunit;

namespace Rag.NET.Parsers.Vision.Tests;

public class RagBuilderExtensionsTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IChatClient>());
        return services;
    }

    [Fact]
    public void UseImageDescription_RegistersIDocumentParser()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseImageDescription()).BuildServiceProvider();
        var parsers = sp.GetServices<IDocumentParser>();
        Assert.Contains(parsers, p => p is ImageDocumentParser);
    }

    [Fact]
    public void UseImageDescription_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseImageDescription()).BuildServiceProvider();
        Assert.IsType<ImageChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseImageDescription_RegistersIChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseImageDescription()).BuildServiceProvider();
        Assert.IsType<ImageChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
    }

    [Fact]
    public void UseImageDescription_CustomOptions_Applied()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseImageDescription(o => o.OcrMinCharacters = 100))
            .BuildServiceProvider();
        Assert.Equal(100, sp.GetRequiredService<ImageDescriptionOptions>().OcrMinCharacters);
    }

    [Fact]
    public void UseVideoDescription_RegistersIDocumentParser()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseVideoDescription()).BuildServiceProvider();
        var parsers = sp.GetServices<IDocumentParser>();
        Assert.Contains(parsers, p => p is VideoDocumentParser);
    }

    [Fact]
    public void UseVideoDescription_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseVideoDescription()).BuildServiceProvider();
        Assert.IsType<VideoChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseVideoDescription_RegistersIChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseVideoDescription()).BuildServiceProvider();
        Assert.IsType<VideoChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
    }

    [Fact]
    public void UseVideoDescription_CustomOptions_Applied()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseVideoDescription(o => o.MaxScenes = 10))
            .BuildServiceProvider();
        Assert.Equal(10, sp.GetRequiredService<VideoDescriptionOptions>().MaxScenes);
    }
}
