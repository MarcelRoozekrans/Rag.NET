using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Pipeline;
using Xunit;

namespace Rag.NET.Tests.Pipeline;

public class RagPipelineFacadeTests
{
    private readonly IRetriever _retriever = Substitute.For<IRetriever>();
    private readonly IIngestor _ingestor = Substitute.For<IIngestor>();
    private readonly IAnswerEngine _answerEngine = Substitute.For<IAnswerEngine>();
    private readonly RagPipeline _sut;

    public RagPipelineFacadeTests()
    {
        _sut = new RagPipeline(_retriever, _ingestor, _answerEngine);
    }

    [Fact]
    public async Task RetrieveAsync_DelegatesToRetriever()
    {
        var expected = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "x", DocumentId = "d", ChunkIndex = 0 }, Score = 1.0 }
        };
        var opts = new RetrievalOptions { TopK = 10 };
        _retriever.RetrieveAsync("query", opts, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task IngestAsync_DelegatesToIngestor()
    {
        var metadata = new DocumentMetadata { DocumentId = "doc-1", FileName = "f.txt", ContentType = "text/plain" };
        var expected = new IngestionResult { DocumentId = "doc-1", ChunksStored = 5 };
        _ingestor.IngestAsync(Arg.Any<Stream>(), metadata, Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        using var stream = new MemoryStream();
        var result = await _sut.IngestAsync(stream, metadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task DeleteAsync_DelegatesToIngestor()
    {
        await _sut.DeleteAsync("doc-1", TestContext.Current.CancellationToken);
        await _ingestor.Received(1).DeleteAsync("doc-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_RetrievesThenDelegatesToAnswerEngine()
    {
        var sources = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "ctx", DocumentId = "d", ChunkIndex = 0 }, Score = 0.9 }
        };
        _retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(sources);

        var expected = new RagResponse { Answer = "The answer", Sources = sources };
        _answerEngine.AskAsync("q", sources, Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.AskAsync("q", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("The answer", result.Answer);
    }

    [Fact]
    public async Task AskAsync_WithoutAnswerEngine_ThrowsInvalidOperationException()
    {
        var sut = new RagPipeline(_retriever, _ingestor, answerEngine: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.AskAsync("q", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AskStreamingAsync_WithoutAnswerEngine_ThrowsInvalidOperationException()
    {
        var sut = new RagPipeline(_retriever, _ingestor, answerEngine: null);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in sut.AskStreamingAsync("q", cancellationToken: TestContext.Current.CancellationToken)) { }
        });
    }
}
