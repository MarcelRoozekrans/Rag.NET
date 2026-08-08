using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Cli.Tests;

/// <summary>
/// Covers <see cref="QueryCommand.RunAsync"/> against a <see cref="FakeRagPipeline"/> — no host,
/// no configuration, no launched process.
/// </summary>
public sealed class QueryCommandTests
{
    [Fact]
    public async Task RunAsync_MapsRetrievedChunksIntoResultItems()
    {
        var chunk = new TextChunk
        {
            Text = "Rag.NET combines retrieval with generation.",
            DocumentId = new DocumentId("doc-1"),
            ChunkIndex = 0,
        };
        var pipeline = new FakeRagPipeline
        {
            RetrieveResult = _ => Result<IReadOnlyList<SearchResult>, RagError>.Success(
                [new SearchResult { Chunk = chunk, Score = 0.87 }]),
        };

        var result = await QueryCommand.RunAsync(
            pipeline, "What is Rag.NET?", topK: 5, TestContext.Current.CancellationToken);

        Assert.Equal("What is Rag.NET?", result.Query);
        var item = Assert.Single(result.Results);
        Assert.Equal(chunk.Text, item.Text);
        Assert.Equal(0.87, item.Score);
    }

    [Fact]
    public async Task RunAsync_PassesTopKThroughToTheRetrievalOptions()
    {
        var pipeline = new FakeRagPipeline();

        _ = await QueryCommand.RunAsync(pipeline, "question", topK: 7, TestContext.Current.CancellationToken);

        var call = Assert.Single(pipeline.RetrieveCalls);
        Assert.Equal(7, call.Options?.TopK);
    }

    [Fact]
    public async Task RunAsync_NoResults_ReturnsAnEmptyResultsList()
    {
        var pipeline = new FakeRagPipeline();

        var result = await QueryCommand.RunAsync(
            pipeline, "question", topK: 5, TestContext.Current.CancellationToken);

        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task RunAsync_PipelineReportsFailure_ThrowsInvalidOperationExceptionNamingIt()
    {
        var pipeline = new FakeRagPipeline
        {
            RetrieveResult = _ => Result<IReadOnlyList<SearchResult>, RagError>.Failure(
                new RagError.NonSeekableStream()),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => QueryCommand.RunAsync(
            pipeline, "question", topK: 5, TestContext.Current.CancellationToken));
        Assert.Contains("Retrieval failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NullPipeline_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => QueryCommand.RunAsync(
            null!, "question", topK: 5, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_WhitespaceQuery_ThrowsArgumentException()
    {
        var pipeline = new FakeRagPipeline();

        await Assert.ThrowsAsync<ArgumentException>(() => QueryCommand.RunAsync(
            pipeline, "   ", topK: 5, TestContext.Current.CancellationToken));
    }
}
