using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Chunking.Semantic;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseSemanticChunkingTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        return services;
    }

    [Fact]
    public void UseSemanticChunking_RegistersIChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticChunking()).BuildServiceProvider();
        Assert.IsType<SemanticChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
    }

    [Fact]
    public void UseSemanticChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticChunking()).BuildServiceProvider();
        Assert.IsType<SemanticChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseSemanticChunking_RegistersIChunkRefinementStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticChunking()).BuildServiceProvider();
        Assert.IsType<SemanticChunkingStrategy>(sp.GetRequiredService<IChunkRefinementStrategy>());
    }

    [Fact]
    public void UseSemanticChunking_AllInterfacesResolveToSameInstance()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticChunking()).BuildServiceProvider();
        var chunking = sp.GetRequiredService<IChunkingStrategy>();
        var docChunking = sp.GetRequiredService<IDocumentChunkingStrategy>();
        var refinement = sp.GetRequiredService<IChunkRefinementStrategy>();

        Assert.Same(chunking, docChunking);
        Assert.Same(chunking, refinement);
    }
}
