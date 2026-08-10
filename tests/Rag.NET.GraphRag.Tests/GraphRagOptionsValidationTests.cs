using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// Registration-time validation for <see cref="GraphRagOptions"/> and
/// <see cref="GraphRagRetrievalOptions"/>, which the issue #90 audit found entirely
/// unvalidated. Same shape as core's <c>ChunkingOptionsValidationTests</c>: the failure
/// happens at the configuring line, not on some later ingestion or retrieval that consumes
/// the singleton.
/// </summary>
public class GraphRagOptionsValidationTests
{
    private static RagBuilder NewBuilder() => new(new ServiceCollection());

    // ---- Ingestion options ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveMaxEntityDescriptionLength_ThrowsAtRegistration(int length)
    {
        // Truncation slices description[..MaxEntityDescriptionLength]: negative threw
        // mid-ingestion on the first extracted entity, zero silently emptied every
        // entity description.
        var ex = Assert.Throws<ArgumentException>(() =>
            NewBuilder().UseGraphRag(o => o.MaxEntityDescriptionLength = length));

        Assert.Contains("MaxEntityDescriptionLength", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroGleaningPasses_RemainsValid()
    {
        // "GleaningPasses = 0 to skip follow-up passes" is the documented cost mitigation —
        // validation must not take it away.
        var builder = NewBuilder();

        builder.UseGraphRag(o => o.GleaningPasses = 0);

        var options = builder.Services.BuildServiceProvider().GetRequiredService<GraphRagOptions>();
        Assert.Equal(0, options.GleaningPasses);
    }

    // ---- Retrieval options ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveLocalSearchDepth_ThrowsAtRegistration(int depth)
    {
        // Traversal loops once per hop, so zero hops collected no neighbors and the PageRank
        // blend silently never applied.
        var ex = Assert.Throws<ArgumentException>(() =>
            NewBuilder().UseGraphRag(retrieval: o => o.LocalSearchDepth = depth));

        Assert.Contains("LocalSearchDepth", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveLocalTopEntities_ThrowsAtRegistration(int topEntities)
    {
        // Take(0) seeded traversal with no entities — local graph search silently disabled.
        var ex = Assert.Throws<ArgumentException>(() =>
            NewBuilder().UseGraphRag(retrieval: o => o.LocalTopEntities = topEntities));

        Assert.Contains("LocalTopEntities", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void PageRankWeightOutsideUnitRange_ThrowsAtRegistration(double weight)
    {
        // The audit's GraphRAG case: (1 − w) × similarity + w × pageRank with w outside [0, 1]
        // gives one term a negative coefficient — ranking silently corrupted.
        var ex = Assert.Throws<ArgumentException>(() =>
            NewBuilder().UseGraphRag(retrieval: o => o.PageRankWeight = weight));

        Assert.Contains("PageRankWeight", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void PageRankWeightAtUnitRangeBounds_IsAccepted(double weight)
    {
        // 0 is "similarity only", 1 is "PageRank only" — both ends are meaningful blends.
        var builder = NewBuilder();

        builder.UseGraphRag(retrieval: o => o.PageRankWeight = weight);

        var options = builder.Services.BuildServiceProvider().GetRequiredService<GraphRagRetrievalOptions>();
        Assert.Equal(weight, options.PageRankWeight);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveGlobalBatchSize_ThrowsAtRegistration(int batchSize)
    {
        // BatchReports advances its loop by this value: zero looped forever — global search
        // hung with no error and no progress (the issue #93 failure shape) — and negative
        // threw when slicing the first batch.
        var ex = Assert.Throws<ArgumentException>(() =>
            NewBuilder().UseGraphRag(retrieval: o => o.GlobalBatchSize = batchSize));

        Assert.Contains("GlobalBatchSize", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullGlobalBatchSize_Passes_AutoBatchingRemainsTheDefault()
    {
        // The nullable-numeric trap this repository hit before (#101): a bare range attribute
        // on int? treats null as 0 and fails it. The When predicate must keep null valid.
        var builder = NewBuilder();

        builder.UseGraphRag();

        var options = builder.Services.BuildServiceProvider().GetRequiredService<GraphRagRetrievalOptions>();
        Assert.Null(options.GlobalBatchSize);
    }

    [Fact]
    public void Defaults_Register()
    {
        var builder = NewBuilder();

        builder.UseGraphRag();

        var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<GraphRagOptions>();
        var retrievalOptions = provider.GetRequiredService<GraphRagRetrievalOptions>();
        Assert.Equal(500, options.MaxEntityDescriptionLength);
        Assert.Equal(0.3, retrievalOptions.PageRankWeight);
        Assert.Equal(1, retrievalOptions.LocalSearchDepth);
    }
}
