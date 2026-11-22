using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Retrieval;

public class PipelineRetrieverResultTests
{
    private static PipelineRetriever CreateSut(
        Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>? pipeline = null) => new()
    {
        Pipeline = pipeline ?? new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(
            (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([]))
    };

    [Fact]
    public async Task RetrieveAsync_PipelineSuccess_ReturnsSuccess()
    {
        IReadOnlyList<SearchResult> expected = [
            new() { Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("d"), ChunkIndex = 0 }, Score = 0.9 }
        ];
        var pipeline = new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(
            (_, _) => ValueTask.FromResult(expected));
        var sut = CreateSut(pipeline);

        var result = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
    }

    [Fact]
    public async Task RetrieveAsync_PipelineThrows_ReturnsStorageFailed()
    {
        var pipeline = new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(
            (_, _) => throw new InvalidOperationException("vector db down"));
        var sut = CreateSut(pipeline);

        var result = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<RagError.StorageFailed>(result.Error);
        Assert.Equal("vector db down", error.Inner.Message);
    }

    /// <summary>
    /// A refusal that reaches the retriever is <see cref="RagError.CallRefused"/>, not a storage
    /// failure: no store was asked anything (#937). Caught by type, so a subclass maps too.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_PipelineThrowsARefusal_ReturnsCallRefused()
    {
        var refusal = new BudgetExceededException(CostWindow.Day, 1m, 1m);
        var pipeline = new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>((_, _) => throw refusal);
        var sut = CreateSut(pipeline);

        var result = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Same(refusal, Assert.IsType<RagError.CallRefused>(result.Error).Inner);
    }
}
