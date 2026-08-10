using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Raptor.Tests;

/// <summary>
/// Registration-time validation for <see cref="RaptorOptions"/> and
/// <see cref="RaptorRetrievalOptions"/>, which the issue #90 audit found entirely unvalidated.
/// Same shape as core's <c>ChunkingOptionsValidationTests</c>: the failure happens at the
/// configuring line, not on some later ingestion or retrieval that consumes the singleton.
/// </summary>
public class RaptorOptionsValidationTests
{
    private static RagBuilder NewBuilder() => new(new ServiceCollection());

    // ---- Ingestion options ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveReducedDimensionality_ThrowsAtRegistration(int dims)
    {
        // Zero reduced every embedding to zero dimensions (nothing to cluster on); negative
        // crashed UMAP's random projection mid-ingestion.
        var ex = Assert.Throws<ArgumentException>(() =>
            NewBuilder().UseRaptor(o => o.ReducedDimensionality = dims));

        Assert.Contains("ReducedDimensionality", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxClustersOfOneOrLower_ThrowsAtRegistration(int maxClusters)
    {
        // BuildLevelAsync stops whenever the effective cluster count is not above 1, so a cap
        // of 1 or lower built no summary levels at all — RAPTOR silently disabled while
        // Enabled still read true.
        var ex = Assert.Throws<ArgumentException>(() =>
            NewBuilder().UseRaptor(o => o.MaxClusters = maxClusters));

        Assert.Contains("MaxClusters", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullMaxClusters_Passes_BicAutoSelectionRemainsTheDefault()
    {
        // The nullable-numeric trap this repository hit before (#101): a bare range attribute
        // on int? treats null as 0 and fails it. The When predicate must keep null valid.
        var builder = NewBuilder();

        builder.UseRaptor();

        var options = builder.Services.BuildServiceProvider().GetRequiredService<RaptorOptions>();
        Assert.Null(options.MaxClusters);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveMaxTreeDepth_ThrowsAtRegistration(int maxTreeDepth)
    {
        // level < MaxTreeDepth gates the first level, so zero built no levels — silently
        // equivalent to Enabled = false.
        var ex = Assert.Throws<ArgumentException>(() =>
            NewBuilder().UseRaptor(o => o.MaxTreeDepth = maxTreeDepth));

        Assert.Contains("MaxTreeDepth", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleLevelTree_MaxTreeDepthOfOne_IsAccepted()
    {
        // The documented cost-mitigation setting ("MaxTreeDepth = 1 for single-level
        // summaries") must stay valid.
        var builder = NewBuilder();

        builder.UseRaptor(o => o.MaxTreeDepth = 1);

        var options = builder.Services.BuildServiceProvider().GetRequiredService<RaptorOptions>();
        Assert.Equal(1, options.MaxTreeDepth);
    }

    [Fact]
    public void NullMaxTreeDepth_Passes_RecursionToOneClusterRemainsTheDefault()
    {
        var builder = NewBuilder();

        builder.UseRaptor(o => o.MaxTreeDepth = null);

        var options = builder.Services.BuildServiceProvider().GetRequiredService<RaptorOptions>();
        Assert.Null(options.MaxTreeDepth);
    }

    // ---- Retrieval options ----

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidSummaryBoostFactor_ThrowsAtRegistration(double factor)
    {
        // ApplyBoost multiplies every summary score by this: zero buried all summaries,
        // negative inverted their order — the opposite of what Boost mode exists to do.
        var ex = Assert.Throws<ArgumentException>(() =>
            NewBuilder().UseRaptor(retrieval: o => o.SummaryBoostFactor = factor));

        Assert.Contains("SummaryBoostFactor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MinLevelAboveMaxLevel_ThrowsAtRegistration()
    {
        // The audit's Filter-mode case: an empty window fails every result against one bound
        // or the other, so retrieval returned nothing, every time, with no error.
        var ex = Assert.Throws<ArgumentException>(() =>
            NewBuilder().UseRaptor(retrieval: o =>
            {
                o.MinRaptorLevel = 2;
                o.MaxRaptorLevel = 1;
            }));

        Assert.Contains("must not exceed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualLevelBounds_AreAccepted()
    {
        // Min == Max is a one-level window — "only level-1 summaries" is a legitimate filter.
        var builder = NewBuilder();

        builder.UseRaptor(retrieval: o =>
        {
            o.MinRaptorLevel = 1;
            o.MaxRaptorLevel = 1;
        });

        var options = builder.Services.BuildServiceProvider().GetRequiredService<RaptorRetrievalOptions>();
        Assert.Equal(1, options.MinRaptorLevel);
        Assert.Equal(1, options.MaxRaptorLevel);
    }

    [Fact]
    public void NegativeMaxRaptorLevel_ThrowsAtRegistration()
    {
        // Levels are never negative (leaves are 0), so a negative upper bound excluded every
        // result in Filter mode.
        var ex = Assert.Throws<ArgumentException>(() =>
            NewBuilder().UseRaptor(retrieval: o => o.MaxRaptorLevel = -1));

        Assert.Contains("MaxRaptorLevel", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleBoundsAndNulls_Pass()
    {
        // Null bounds are the documented "no bound" configuration, and each bound alone is
        // valid: MaxRaptorLevel = 0 is the documented "only leaf chunks" filter.
        var builder = NewBuilder();

        builder.UseRaptor(retrieval: o =>
        {
            o.MinRaptorLevel = null;
            o.MaxRaptorLevel = 0;
        });

        var options = builder.Services.BuildServiceProvider().GetRequiredService<RaptorRetrievalOptions>();
        Assert.Null(options.MinRaptorLevel);
        Assert.Equal(0, options.MaxRaptorLevel);
    }

    [Fact]
    public void TheGeneratedValidatorItselfEnforcesTheLevelWindowRule()
    {
        // Anyone holding only the validator must get the same answer as the builder — the
        // reason ValidateLevelWindow is a [CustomValidation] method, not a check UseRaptor
        // happens to remember (the ChunkingOptions.ValidateOverlapFitsChunk lesson).
        var result = new RaptorRetrievalOptionsValidator()
            .Validate(new RaptorRetrievalOptions { MinRaptorLevel = 3, MaxRaptorLevel = 2 });

        Assert.False(result.IsValid);

        var failures = result.Failures;
        var reported = new string[failures.Length];
        for (var i = 0; i < failures.Length; i++)
        {
            reported[i] = failures[i].PropertyName;
        }

        Assert.Contains(nameof(RaptorRetrievalOptions.MinRaptorLevel), reported, StringComparer.Ordinal);
    }

    [Fact]
    public void Defaults_Register()
    {
        var builder = NewBuilder();

        builder.UseRaptor();

        var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<RaptorOptions>();
        var retrievalOptions = provider.GetRequiredService<RaptorRetrievalOptions>();
        Assert.Equal(10, options.ReducedDimensionality);
        Assert.Equal(1.2, retrievalOptions.SummaryBoostFactor);
    }
}
