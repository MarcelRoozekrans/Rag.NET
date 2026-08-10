using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// Registration-time validation for the retriever-decorator options the issue #90 audit found
/// entirely unvalidated: <see cref="DeepResearchOptions"/>, <see cref="TagRetrievalOptions"/>,
/// and <see cref="TimeWeightedOptions"/>. Same shape as
/// <see cref="ChunkingOptionsValidationTests"/>: the failure happens at the configuring line,
/// not on some later retrieval that happens to consume the singleton.
/// </summary>
public class RetrievalDecoratorOptionsValidationTests
{
    private static ServiceCollection NewServices() => new();

    // ---- UseDeepResearch ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DeepResearch_NonPositiveMaxDepth_ThrowsAtRegistration(int maxDepth)
    {
        var services = NewServices();

        // MaxDepth <= 0 used to register happily and then skip the research loop entirely —
        // deep research silently degraded to plain retrieval.
        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddRagNet(rag => rag.UseDeepResearch(new DeepResearchOptions { MaxDepth = maxDepth })));

        Assert.Contains("MaxDepth", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DeepResearch_NonPositiveSubQueryCount_ThrowsAtRegistration(int subQueryCount)
    {
        var services = NewServices();

        // Zero burned one LLM sufficiency call per iteration while retrieving nothing;
        // negative threw from raw[..subCount] mid-retrieval on the first insufficiency.
        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddRagNet(rag => rag.UseDeepResearch(new DeepResearchOptions { SubQueryCount = subQueryCount })));

        Assert.Contains("SubQueryCount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepResearch_Defaults_Register()
    {
        var services = NewServices();

        services.AddRagNet(rag => rag.UseDeepResearch());

        var options = services.BuildServiceProvider().GetRequiredService<DeepResearchOptions>();
        Assert.Equal(3, options.MaxDepth);
        Assert.Equal(3, options.SubQueryCount);
    }

    // ---- UseTagRetrieval ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TagRetrieval_NonPositiveTopK_ThrowsAtRegistration(int topK)
    {
        var services = NewServices();

        // With TopK <= 0 the retriever still embedded the query and scanned the index, but the
        // injection cap could never admit a filter — tag retrieval silently failed open.
        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddRagNet(rag => rag.UseTagRetrieval(new TagRetrievalOptions { TopK = topK })));

        Assert.Contains("TopK", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-1.5)]
    [InlineData(double.NaN)]
    public void TagRetrieval_MinScoreOutsideCosineRange_ThrowsAtRegistration(double minScore)
    {
        var services = NewServices();

        // Above 1 no cosine similarity can ever meet the threshold, so no filter was ever
        // injected — the same silent fail-open, from the other property.
        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddRagNet(rag => rag.UseTagRetrieval(new TagRetrievalOptions { MinScore = minScore })));

        Assert.Contains("MinScore", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    public void TagRetrieval_MinScoreWithinCosineRange_IsAccepted(double minScore)
    {
        var services = NewServices();

        // Both ends of the cosine range are meaningful: 1.0 injects only exact matches,
        // -1.0 injects everything the index holds.
        services.AddRagNet(rag => rag.UseTagRetrieval(new TagRetrievalOptions { MinScore = minScore }));

        var options = services.BuildServiceProvider().GetRequiredService<TagRetrievalOptions>();
        Assert.Equal(minScore, options.MinScore);
    }

    [Fact]
    public void TagRetrieval_Defaults_Register()
    {
        var services = NewServices();

        services.AddRagNet(rag => rag.UseTagRetrieval());

        var options = services.BuildServiceProvider().GetRequiredService<TagRetrievalOptions>();
        Assert.Equal(1, options.TopK);
        Assert.Equal(0.82, options.MinScore);
    }

    // ---- UseTimeWeighting ----

    [Fact]
    public void TimeWeighting_NegativeDecayRate_ThrowsAtRegistration()
    {
        var services = NewServices();

        // The audit's headline case: e^(−λ × age) with λ < 0 grows exponentially with age, so
        // the oldest content in the store outranked everything — recency silently inverted.
        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddRagNet(rag => rag.UseTimeWeighting(new TimeWeightedOptions { DecayRate = -0.01 })));

        Assert.Contains("DecayRate", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TimeWeighting_NonFiniteDecayRate_ThrowsAtRegistration(double decayRate)
    {
        var services = NewServices();

        // NaN poisons every rescored result; +∞ zeroes every aged result and turns a
        // zero-age chunk's factor into NaN (∞ × 0).
        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddRagNet(rag => rag.UseTimeWeighting(new TimeWeightedOptions { DecayRate = decayRate })));

        Assert.Contains("DecayRate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeWeighting_ZeroDecayRate_IsAccepted()
    {
        var services = NewServices();

        // Zero means no decay — every result keeps its original score. Valid, like
        // ChunkingOptions.Overlap = 0: the boundary belongs inside the range.
        services.AddRagNet(rag => rag.UseTimeWeighting(new TimeWeightedOptions { DecayRate = 0.0 }));

        var options = services.BuildServiceProvider().GetRequiredService<TimeWeightedOptions>();
        Assert.Equal(0.0, options.DecayRate);
    }

    [Fact]
    public void TimeWeighting_Defaults_Register()
    {
        var services = NewServices();

        services.AddRagNet(rag => rag.UseTimeWeighting());

        var options = services.BuildServiceProvider().GetRequiredService<TimeWeightedOptions>();
        Assert.Equal(0.01, options.DecayRate);
    }
}
