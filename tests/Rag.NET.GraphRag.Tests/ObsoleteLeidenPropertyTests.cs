using System.Reflection;
using Rag.NET.Graph.Algorithms;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// Exercises <c>GraphRagOptions.Leiden</c>, the forwarder left behind when the ingestion options'
/// clustering settings were renamed to <c>CommunityDetection</c> along with the algorithm.
/// </summary>
/// <remarks>
/// Reflection, not source, for the same reason as <c>ObsoleteLeidenForwarderTests</c> over in
/// <c>Rag.NET.Graph.Tests</c>: this tree builds with <c>TreatWarningsAsErrors</c>, so naming the
/// deprecated property in a test would turn its own deprecation warning into a build error. What is
/// asserted is that the old property is the same storage as the new one in both directions — a
/// forwarder that only forwarded its getter would leave a caller's <c>o.Leiden.Resolution = 2.5</c>
/// silently unread by the clustering, which is the exact defect audit #108 found three times.
/// </remarks>
public class ObsoleteLeidenPropertyTests
{
    [Fact]
    public void WritingTheObsoletePropertyIsWritingTheNewOne()
    {
        var options = new GraphRagOptions();
        var replacement = new LouvainWithRefinementOptions { Resolution = 2.5, RandomSeed = 7 };

        ObsoleteProperty().SetValue(options, replacement);

        Assert.Same(replacement, options.CommunityDetection);
        Assert.Same(replacement, ObsoleteProperty().GetValue(options));
    }

    [Fact]
    public void ReadingTheObsoletePropertyReadsTheNewOne()
    {
        var options = new GraphRagOptions();
        options.CommunityDetection.Resolution = 3.5;

        var throughOldName = Assert.IsType<LouvainWithRefinementOptions>(
            ObsoleteProperty().GetValue(options));

        Assert.Equal(3.5, throughOldName.Resolution);
    }

    [Fact]
    public void TheObsoletePropertySaysWhatToUseAndWhyTheOldNameWasWrong()
    {
        var attribute = ObsoleteProperty().GetCustomAttribute<ObsoleteAttribute>();

        Assert.NotNull(attribute);
        Assert.False(
            attribute.IsError,
            "GraphRagOptions.Leiden is marked obsolete as an error, which breaks every caller on " +
            "0.1.0 outright — the deprecation path is the whole point of keeping the name.");

        var message = attribute.Message ?? string.Empty;
        Assert.Contains("CommunityDetection", message, StringComparison.Ordinal);

        // See ObsoleteLeidenForwarderTests for why the pinned phrase changed with #180: the message
        // used to assert the missing guarantee, and outlasted the defect it described.
        Assert.Contains("the Leiden paper's refinement phase", message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not provide", message, StringComparison.Ordinal);
    }

    /// <summary>Resolves the deprecated property by name, so no source file has to name it.</summary>
    /// <returns>The property.</returns>
    private static PropertyInfo ObsoleteProperty()
    {
        var property = typeof(GraphRagOptions).GetProperty("Leiden", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(property);
        return property;
    }
}
