using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class PipelineRetrieverValidationTests
{
    private static PipelineRetriever CreateSut() => new()
    {
        Pipeline = new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(
            (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([]))
    };

    [Fact]
    public async Task RetrieveAsync_ZeroTopK_ReturnsValidationFailed()
    {
        var sut = CreateSut();
        var options = new RetrievalOptions { TopK = 0 };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("TopK", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RetrieveAsync_NegativeTopK_ReturnsValidationFailed()
    {
        var sut = CreateSut();
        var options = new RetrievalOptions { TopK = -1 };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.IsType<RagError.ValidationFailed>(result.Error);
    }

    [Fact]
    public async Task RetrieveAsync_ValidOptions_Succeeds()
    {
        var sut = CreateSut();

        var result = await sut.RetrieveAsync("query", new RetrievalOptions { TopK = 5 }, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task RetrieveAsync_NullOptions_Succeeds()
    {
        var sut = CreateSut();

        var result = await sut.RetrieveAsync("query", null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task RedundancyThreshold_BelowZero_ReturnsFailed()
    {
        var sut = CreateSut();
        var options = new RetrievalOptions { RedundancyThreshold = -0.1f };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("RedundancyThreshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RedundancyThreshold_AboveOne_ReturnsFailed()
    {
        var sut = CreateSut();
        var options = new RetrievalOptions { RedundancyThreshold = 1.1f };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("RedundancyThreshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MmrLambda_BelowZero_ReturnsFailed()
    {
        var sut = CreateSut();
        var options = new RetrievalOptions { MmrLambda = -0.1f };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("MmrLambda", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MmrLambda_AboveOne_ReturnsFailed()
    {
        var sut = CreateSut();
        var options = new RetrievalOptions { MmrLambda = 1.1f };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("MmrLambda", StringComparison.OrdinalIgnoreCase));
    }

    // The checks below were added with DocumentedConstraintGuardTests, which proves a constraint
    // is enforced *somewhere* but not that the enforcement is correct. These pin the behaviour:
    // out-of-range values must be rejected, and the boundaries must stay inclusive.

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    public async Task RetrieveAsync_CragScoreThresholdOutOfRange_ReturnsValidationFailed(float threshold)
    {
        var sut = CreateSut();
        var options = new RetrievalOptions { CragScoreThreshold = threshold };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(
            error.Failures,
            f => f.PropertyName.Contains("CragScoreThreshold", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(1.0f)]
    public async Task RetrieveAsync_CragScoreThresholdAtBoundary_Succeeds(float threshold)
    {
        var sut = CreateSut();

        var result = await sut.RetrieveAsync(
            "query",
            new RetrievalOptions { CragScoreThreshold = threshold },
            TestContext.Current.CancellationToken);

        // An exclusive bound here would silently disable CRAG at 0 and fire it on every query
        // at 1 — the two values a caller is most likely to reach for.
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(-0.1f, 0.5f, "DenseWeight")]
    [InlineData(1.1f, 0.5f, "DenseWeight")]
    [InlineData(0.5f, -0.1f, "Bm25Weight")]
    [InlineData(0.5f, 1.1f, "Bm25Weight")]
    public async Task RetrieveAsync_EnsembleWeightOutOfRange_ReturnsValidationFailed(
        float dense, float bm25, string expectedProperty)
    {
        var sut = CreateSut();
        var options = new RetrievalOptions
        {
            EnsembleOptions = new EnsembleOptions { DenseWeight = dense, Bm25Weight = bm25 },
        };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(
            error.Failures,
            f => f.PropertyName.Contains(expectedProperty, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RetrieveAsync_NoEnsembleOptions_SkipsWeightValidation()
    {
        var sut = CreateSut();

        // EnsembleOptions is nullable and unset by default; validating a null must not throw.
        var result = await sut.RetrieveAsync(
            "query",
            new RetrievalOptions { EnsembleOptions = null },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }
}
