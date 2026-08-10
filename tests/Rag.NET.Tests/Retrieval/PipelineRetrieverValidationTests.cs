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

    // The tests below pin the move from the hand-written check block to the generated
    // RetrievalOptionsValidator (issue #100), and the issue #94 properties it newly covers.

    [Fact]
    public async Task RetrieveAsync_MultipleInvalidValues_ReportsEveryFailureAtOnce()
    {
        // The replaced manual block early-returned on the first failure, so a caller with three
        // bad values learned about one. The generated validator reports all of them.
        var sut = CreateSut();
        var options = new RetrievalOptions { TopK = 0, RedundancyThreshold = 1.1f, MmrLambda = -0.1f };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Equal(3, error.Failures.Count);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("TopK", StringComparison.Ordinal));
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("RedundancyThreshold", StringComparison.Ordinal));
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("MmrLambda", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RetrieveAsync_NonPositiveCandidateCount_ReturnsValidationFailed(int candidateCount)
    {
        // Issue #94: RerankingBehavior overwrites the downstream TopK with CandidateCount, and
        // the store returns [] for a non-positive limit — a validated TopK silently replaced by
        // an unvalidated 0. Rejected here instead.
        var sut = CreateSut();
        var options = new RetrievalOptions { CandidateCount = candidateCount };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("CandidateCount", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RetrieveAsync_NonPositiveMmrCandidateCount_ReturnsValidationFailed(int mmrCandidateCount)
    {
        var sut = CreateSut();
        var options = new RetrievalOptions { MmrCandidateCount = mmrCandidateCount };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("MmrCandidateCount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetrieveAsync_NullCandidateCounts_Succeed()
    {
        // Null means "derive from TopK" and must pass. This is the nullable-int trap: a bare
        // [GreaterThan(0)] on int? converts null to 0 and rejects it, so the attributes carry
        // a When-condition that skips the bound when the value is unset.
        var sut = CreateSut();
        var options = new RetrievalOptions { CandidateCount = null, MmrCandidateCount = null };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public async Task RetrieveAsync_PositiveCandidateCounts_Succeed(int count)
    {
        var sut = CreateSut();
        var options = new RetrievalOptions { CandidateCount = count, MmrCandidateCount = count };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task RetrieveAsync_NonFiniteMinScore_ReturnsValidationFailed(double minScore)
    {
        var sut = CreateSut();
        var options = new RetrievalOptions { MinScore = minScore };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("MinScore", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(-1.0)]
    public async Task RetrieveAsync_FiniteMinScoreOutsideZeroToOne_Succeeds(double minScore)
    {
        // Deliberate: inner-product stores return unbounded scores and OpaqueRanking stores
        // return RRF values on another scale entirely, so only non-finite values are rejected.
        // A zero-to-one bound here would reject legitimate configurations (issue #94).
        var sut = CreateSut();
        var options = new RetrievalOptions { MinScore = minScore };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    public async Task RetrieveAsync_SparseWeightOutOfRange_ReturnsValidationFailed(float sparseWeight)
    {
        var sut = CreateSut();
        var options = new RetrievalOptions
        {
            EnsembleOptions = new EnsembleOptions { SparseWeight = sparseWeight },
        };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("SparseWeight", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public async Task RetrieveAsync_NonPositiveRrfK_ReturnsValidationFailed(int k)
    {
        // RrfMerger used to silently clamp a non-positive k to 1, so the configured value and
        // the effective value disagreed with no signal — the issue #94 failure mode.
        var sut = CreateSut();
        var options = new RetrievalOptions { EnsembleOptions = new EnsembleOptions { K = k } };

        var result = await sut.RetrieveAsync("query", options, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.ValidationFailed>(result.Error);
        Assert.Contains(error.Failures, f => f.PropertyName.Contains(".K", StringComparison.Ordinal));
    }

    [Fact]
    public void RetrievalOptionsValidator_BadNestedWeight_ReportsThroughParentValidator()
    {
        // Proves the nested mechanism itself, not just the pipeline wiring: the generated
        // RetrievalOptionsValidator takes the EnsembleOptionsValidator in its constructor and
        // prefixes nested failures with the property name.
        var validator = new RetrievalOptionsValidator(new EnsembleOptionsValidator());
        var options = new RetrievalOptions { EnsembleOptions = new EnsembleOptions { DenseWeight = 1.5f } };

        var result = validator.Validate(options);

        Assert.False(result.IsValid);
        var failures = result.Failures;
        Assert.Equal(1, failures.Length);
        Assert.Equal("EnsembleOptions.DenseWeight", failures[0].PropertyName);
    }
}
