using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// Registration-time validation for <see cref="GraphRagOptions"/> and
/// <see cref="GraphRagGlobalSearchOptions"/>, which the issue #90 audit found entirely
/// unvalidated. Same shape as core's <c>ChunkingOptionsValidationTests</c>: the failure
/// happens at the configuring line, not on some later ingestion or retrieval that consumes
/// the singleton.
/// </summary>
public class GraphRagOptionsValidationTests
{
    private static RagBuilder NewBuilder() => ConfiguredRagBuilder.Create();

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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCommunityReportConcurrency_ThrowsAtRegistration(int concurrency)
    {
        // #226: the bound on how many community-report calls are in flight. Zero would hand
        // Parallel.ForEachAsync a degree of parallelism it rejects mid-ingestion, on the first
        // corpus large enough to have communities; negative would read as "unbounded" to that API
        // and turn a 3,587-community graph into 3,587 simultaneous requests at the provider.
        var ex = Assert.Throws<ArgumentException>(() =>
            NewBuilder().UseGraphRag(o => o.CommunityReportConcurrency = concurrency));

        Assert.Contains("CommunityReportConcurrency", ex.Message, StringComparison.Ordinal);
    }

    // ---- Retrieval options ----

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

        var options = builder.Services.BuildServiceProvider().GetRequiredService<GraphRagGlobalSearchOptions>();
        Assert.Null(options.GlobalBatchSize);
    }

    [Fact]
    public void Defaults_Register()
    {
        var builder = NewBuilder();

        builder.UseGraphRag();

        var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<GraphRagOptions>();
        Assert.Equal(500, options.MaxEntityDescriptionLength);
    }
}
