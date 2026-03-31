using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Chunking.Semantic;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseSemanticRefinementTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        return services;
    }

    [Fact]
    public void UseSemanticRefinement_RegistersIChunkRefinementStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticRefinement()).BuildServiceProvider();
        Assert.IsType<SemanticChunkingStrategy>(sp.GetRequiredService<IChunkRefinementStrategy>());
    }

    [Fact]
    public void UseSemanticRefinement_DoesNotOverrideIChunkingStrategyWithSemantic()
    {
        // UseSemanticRefinement should not register SemanticChunkingStrategy as IChunkingStrategy.
        // The default RecursiveChunkingStrategy (from AddRagNETServices) should remain.
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticRefinement()).BuildServiceProvider();
        Assert.IsNotType<SemanticChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
    }

    [Fact]
    public void UseSemanticRefinement_DoesNotRegisterIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticRefinement()).BuildServiceProvider();
        Assert.Null(sp.GetService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseHierarchicalMerging_ThenUseSemanticRefinement_BothRegistered()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag
                .UseHierarchicalMerging()
                .UseSemanticRefinement())
            .BuildServiceProvider();

        // IChunkingStrategy resolves to HierarchicalMergerChunkingStrategy (not SemanticChunkingStrategy)
        Assert.IsType<HierarchicalMergerChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
        // IChunkRefinementStrategy resolves to SemanticChunkingStrategy
        Assert.IsType<SemanticChunkingStrategy>(sp.GetRequiredService<IChunkRefinementStrategy>());
    }
}
