using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseHierarchicalMergingTests
{
    [Fact]
    public void UseHierarchicalMerging_RegistersStrategyAsIChunkingStrategy()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseHierarchicalMerging());

        var sp = services.BuildServiceProvider();
        var strategy = sp.GetRequiredService<IChunkingStrategy>();

        Assert.IsType<HierarchicalMergerChunkingStrategy>(strategy);
    }

    [Fact]
    public void UseHierarchicalMerging_WithOptions_RegistersOptionsAsSingleton()
    {
        var opts = new HierarchicalMergerOptions { MaxDepth = 3 };
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseHierarchicalMerging(opts));

        var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<HierarchicalMergerOptions>();

        Assert.Equal(3, resolved.MaxDepth);
    }

    [Fact]
    public void Strategy_ImplementsIDocumentChunkingStrategy_TypeCheck()
    {
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseHierarchicalMerging());

        var sp = services.BuildServiceProvider();
        var strategy = sp.GetRequiredService<IChunkingStrategy>();

        // Type-check only — UseHierarchicalMerging registers IChunkingStrategy, not IDocumentChunkingStrategy.
        // ParseBehavior discovers document-level chunking via a runtime cast, not DI resolution.
        Assert.IsAssignableFrom<IDocumentChunkingStrategy>(strategy);
    }
}
