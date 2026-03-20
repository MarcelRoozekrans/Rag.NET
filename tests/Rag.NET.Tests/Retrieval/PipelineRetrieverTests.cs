using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class PipelineRetrieverTests
{
    private static PipelineRetriever CreateSut(
        Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>? pipeline = null) =>
        new()
        {
            Pipeline = pipeline ?? new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(
                (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([])),
        };

    [Fact]
    public async Task RetrieveAsync_CreatesContextAndExecutesPipeline()
    {
        var capturedCtx = (RetrievalContext?)null;
        var pipeline = new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>((ctx, _) =>
        {
            capturedCtx = ctx;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
        });
        var sut = CreateSut(pipeline);
        var options = new RetrievalOptions { TopK = 5 };

        _ = await sut.RetrieveAsync("my query", options, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedCtx);
        Assert.Equal("my query", capturedCtx!.Query);
        Assert.Equal(5, capturedCtx.Options.TopK);
    }

    [Fact]
    public async Task RetrieveAsync_WithNullOptions_UsesDefaultOptions()
    {
        RetrievalContext? captured = null;
        var sut = CreateSut(new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>((ctx, _) =>
        {
            captured = ctx;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
        }));

        _ = await sut.RetrieveAsync("q", null, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Options);
    }
}
