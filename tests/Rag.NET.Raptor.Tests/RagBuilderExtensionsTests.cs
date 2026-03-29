using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Raptor.Tests;

public class RagBuilderExtensionsTests
{
    [Fact]
    public void UseRaptor_RegistersOptionsAsSingleton()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseRaptor();

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorOptions));
        Assert.Contains(services, d => d.ServiceType == typeof(RaptorRetrievalOptions));
    }

    [Fact]
    public void UseRaptor_WithConfigure_AppliesOptions()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseRaptor(o => o.MinChunksForRaptor = 42);

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<RaptorOptions>();
        Assert.Equal(42, opts.MinChunksForRaptor);
    }

    [Fact]
    public void UseRaptor_WithRetrievalConfigure_AppliesOptions()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseRaptor(retrieval: o => o.Mode = RaptorRetrievalMode.Boost);

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<RaptorRetrievalOptions>();
        Assert.Equal(RaptorRetrievalMode.Boost, opts.Mode);
    }

    [Fact]
    public void UseRaptor_RegistersIngestionBehavior()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseRaptor();

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorIngestionBehavior));
    }

    [Fact]
    public void UseRaptor_RegistersRetrievalBehavior()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseRaptor();

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorRetrievalBehavior));
    }
}
