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
}
