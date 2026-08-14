using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Raptor.Tests;

public class RagBuilderExtensionsTests
{
    [Fact]
    public void UseRaptor_RegistersOptionsAsSingleton()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor();

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorOptions));
        Assert.Contains(services, d => d.ServiceType == typeof(RaptorRetrievalOptions));
    }

    [Fact]
    public void UseRaptor_WithConfigure_AppliesOptions()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor(o => o.MinChunksForRaptor = 42);

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<RaptorOptions>();
        Assert.Equal(42, opts.MinChunksForRaptor);
    }

    [Fact]
    public void UseRaptor_WithRetrievalConfigure_AppliesOptions()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor(retrieval: o => o.Mode = RaptorRetrievalMode.Boost);

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<RaptorRetrievalOptions>();
        Assert.Equal(RaptorRetrievalMode.Boost, opts.Mode);
    }

    [Fact]
    public void UseRaptor_RegistersIngestionBehavior()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor();

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorIngestionBehavior));
    }

    [Fact]
    public void UseRaptor_ReturnsBuilderForChaining()
    {
        var builder = ConfiguredRagBuilder.Create();

        var result = builder.UseRaptor();

        Assert.Same(builder, result);
    }

    [Fact]
    public void UseRaptor_RegistersRetrievalBehavior()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor();

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorRetrievalBehavior));
    }
}
