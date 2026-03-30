using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Rag.NET.Graph;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

public class RagBuilderExtensionsTests
{
    [Fact]
    public void UseGraphRag_RegistersOptions()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseGraphRag();

        Assert.Contains(services, d => d.ServiceType == typeof(GraphRagOptions));
        Assert.Contains(services, d => d.ServiceType == typeof(GraphRagRetrievalOptions));
    }

    [Fact]
    public void UseGraphRag_ConfigureDelegateApplied()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseGraphRag(o => o.GleaningPasses = 5);

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<GraphRagOptions>();
        Assert.Equal(5, opts.GleaningPasses);
    }

    [Fact]
    public void UseGraphRag_RegistersGraphStore()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseGraphRag();

        Assert.Contains(services, d => d.ServiceType == typeof(IGraphStore));
    }

    [Fact]
    public void UseGraphRag_RegistersAllBehaviors()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseGraphRag();

        Assert.Contains(services, d => d.ServiceType == typeof(GraphEntityExtractionBehavior));
        Assert.Contains(services, d => d.ServiceType == typeof(CommunityDetectionBehavior));
        Assert.Contains(services, d => d.ServiceType == typeof(GraphLocalSearchBehavior));
        Assert.Contains(services, d => d.ServiceType == typeof(GraphGlobalSearchBehavior));
    }

    [Fact]
    public void UseGraphRag_ReturnsBuilderForChaining()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        var result = builder.UseGraphRag();

        Assert.Same(builder, result);
    }
}
