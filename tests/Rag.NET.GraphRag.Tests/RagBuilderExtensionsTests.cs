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
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseGraphRag();

        Assert.Contains(services, d => d.ServiceType == typeof(GraphRagOptions));
        Assert.Contains(services, d => d.ServiceType == typeof(GraphRagGlobalSearchOptions));
    }

    [Fact]
    public void UseGraphRag_ConfigureDelegateApplied()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseGraphRag(o => o.GleaningPasses = 5);

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<GraphRagOptions>();
        Assert.Equal(5, opts.GleaningPasses);
    }

    /// <summary>Leiden's own settings are reachable from the package's public API.</summary>
    /// <remarks>
    /// <b>They were not, and nothing said so.</b> <c>LeidenOptions</c> shipped public, documented
    /// and complete with a resolution parameter — and <c>CommunityDetectionBehavior</c> called
    /// <c>Leiden.Detect(snapshot)</c> with the argument omitted, so every knob on it was
    /// unreachable through <c>UseGraphRag</c> and the defaults were the only settings that had ever
    /// run. That is the exact shape of the three dead settings audit #108 found.
    /// </remarks>
    [Fact]
    public void UseGraphRag_LeidenOptionsAreConfigurable()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseGraphRag(o =>
        {
            o.Leiden.Resolution = 2.5;
            o.Leiden.RandomSeed = 7;
        });

        var opts = services.BuildServiceProvider().GetRequiredService<GraphRagOptions>();
        Assert.Equal(2.5, opts.Leiden.Resolution);
        Assert.Equal(7, opts.Leiden.RandomSeed);
    }

    /// <summary>A Leiden setting that would misbehave is rejected where it was configured.</summary>
    /// <remarks>
    /// The same registration-time contract every other option here has. A resolution of zero makes
    /// modularity's penalty term vanish and every graph collapse to one community — which is
    /// precisely the failure this milestone spent two commits chasing, and it should not be
    /// reachable by configuration once it has been fixed in the algorithm.
    /// </remarks>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void UseGraphRag_NonPositiveLeidenResolution_ThrowsAtTheConfiguringLine(double resolution)
    {
        var builder = ConfiguredRagBuilder.Create();

        var ex = Assert.Throws<ArgumentException>(
            () => builder.UseGraphRag(o => o.Leiden.Resolution = resolution));

        Assert.Equal("configure", ex.ParamName);
        Assert.Contains("Leiden", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseGraphRag_RegistersGraphStore()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseGraphRag();

        Assert.Contains(services, d => d.ServiceType == typeof(IGraphStore));
    }

    [Fact]
    public void UseGraphRag_RegistersAllBehaviors()
    {
        var builder = ConfiguredRagBuilder.Create();
        var services = builder.Services;

        builder.UseGraphRag();

        Assert.Contains(services, d => d.ServiceType == typeof(GraphEntityExtractionBehavior));
        Assert.Contains(services, d => d.ServiceType == typeof(CommunityDetectionBehavior));
        Assert.Contains(services, d => d.ServiceType == typeof(GraphGlobalSearchBehavior));
    }

    [Fact]
    public void UseGraphRag_ReturnsBuilderForChaining()
    {
        var builder = ConfiguredRagBuilder.Create();

        var result = builder.UseGraphRag();

        Assert.Same(builder, result);
    }
}
