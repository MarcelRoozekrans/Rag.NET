using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Raptor.Tests;

/// <summary>
/// These tests are about <c>UseRaptor</c>'s registration mechanics, not <c>TreeScope</c>, so every
/// call here sets <c>TreeScope = PerDocument</c> explicitly — the default is now <c>Corpus</c>,
/// which requires <c>leafStorePath</c> and would otherwise fail every one of these at the
/// <c>UseRaptor</c> line before it got anywhere near what each test actually checks (#331).
/// </summary>
public class RagBuilderExtensionsTests
{
    [Fact]
    public void UseRaptor_RegistersOptionsAsSingleton()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor(o => o.TreeScope = RaptorTreeScope.PerDocument);

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorOptions));
        Assert.Contains(services, d => d.ServiceType == typeof(RaptorRetrievalOptions));
    }

    [Fact]
    public void UseRaptor_WithConfigure_AppliesOptions()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor(o =>
        {
            o.TreeScope = RaptorTreeScope.PerDocument;
            o.MinChunksForRaptor = 42;
        });

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<RaptorOptions>();
        Assert.Equal(42, opts.MinChunksForRaptor);
    }

    [Fact]
    public void UseRaptor_WithRetrievalConfigure_AppliesOptions()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor(
            o => o.TreeScope = RaptorTreeScope.PerDocument,
            retrieval: o => o.Mode = RaptorRetrievalMode.Boost);

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<RaptorRetrievalOptions>();
        Assert.Equal(RaptorRetrievalMode.Boost, opts.Mode);
    }

    [Fact]
    public void UseRaptor_RegistersIngestionBehavior()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor(o => o.TreeScope = RaptorTreeScope.PerDocument);

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorIngestionBehavior));
    }

    [Fact]
    public void UseRaptor_ReturnsBuilderForChaining()
    {
        var builder = ConfiguredRagBuilder.Create();

        var result = builder.UseRaptor(o => o.TreeScope = RaptorTreeScope.PerDocument);

        Assert.Same(builder, result);
    }

    [Fact]
    public void UseRaptor_RegistersRetrievalBehavior()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseRaptor(o => o.TreeScope = RaptorTreeScope.PerDocument);

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorRetrievalBehavior));
    }
}
