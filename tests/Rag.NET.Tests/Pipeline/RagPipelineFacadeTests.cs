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
            new() { Chunk = new TextChunk { Text = "x", DocumentId = new DocumentId("d"), ChunkIndex = 0 }, Score = 1.0 }
        };
        var opts = new RetrievalOptions { TopK = 10 };
        _retriever.RetrieveAsync("query", opts, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task IngestAsync_DelegatesToIngestor()
    {
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "f.txt", ContentType = "text/plain" };
        var expected = new IngestionResult { DocumentId = new DocumentId("doc-1"), ChunksStored = 5 };
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
            new() { Chunk = new TextChunk { Text = "ctx", DocumentId = new DocumentId("d"), ChunkIndex = 0 }, Score = 0.9 }
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

    [Fact]
    public async Task AskStreamingAsync_RetrievesThenStreamsUpdates()
    {
        var ct = TestContext.Current.CancellationToken;
        var sources = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "ctx", DocumentId = new DocumentId("d"), ChunkIndex = 0 }, Score = 0.9 }
        };
        _retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), ct)
            .Returns(sources);

        var updates = new List<RagStreamingUpdate>
        {
            new() { Sources = sources },
            new() { TextDelta = "Hello " },
            new() { TextDelta = "world" },
        };
        _answerEngine.AskStreamingAsync("q", sources, Arg.Any<RagOptions?>(), ct)
            .Returns(StreamingUpdatesFrom(updates));

        var received = new List<RagStreamingUpdate>();
        await foreach (var update in _sut.AskStreamingAsync("q", cancellationToken: ct))
            received.Add(update);

        Assert.Equal(3, received.Count);
        Assert.Same(sources, received[0].Sources);
        Assert.Equal("Hello ", received[1].TextDelta);
        Assert.Equal("world", received[2].TextDelta);
    }

    [Fact]
    public async Task AskStreamingAsync_MapsRagOptionsToRetrievalOptions()
    {
        var ct = TestContext.Current.CancellationToken;
        _retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), ct)
            .Returns(new List<SearchResult>());
        _answerEngine.AskStreamingAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<RagOptions?>(), ct)
            .Returns(EmptyStreamingUpdates());

        var opts = new RagOptions
        {
            TopK = 7,
            MinScore = 0.5,
            UseHybridSearch = true,
            UseLostInTheMiddleReordering = true,
            UseRedundancyFilter = true,
            RedundancyThreshold = 0.8f,
            MetadataFilter = new Dictionary<string, string>(StringComparer.Ordinal) { ["key"] = "val" },
        };
        await foreach (var _ in _sut.AskStreamingAsync("q", opts, ct)) { }

        await _retriever.Received(1).RetrieveAsync(
            "q",
            Arg.Is<RetrievalOptions?>(r =>
                r != null &&
                r.TopK == 7 &&
                r.MinScore == 0.5 &&
                r.UseHybridSearch &&
                r.UseLostInTheMiddleReordering &&
                r.UseRedundancyFilter &&
                r.RedundancyThreshold == 0.8f &&
                r.MetadataFilter != null &&
                r.MetadataFilter.ContainsKey("key")),
            ct);
    }

    private static async IAsyncEnumerable<RagStreamingUpdate> StreamingUpdatesFrom(IEnumerable<RagStreamingUpdate> items)
    {
        foreach (var item in items)
        {
            await Task.CompletedTask;
            yield return item;
        }
    }

    private static IAsyncEnumerable<RagStreamingUpdate> EmptyStreamingUpdates()
        => StreamingUpdatesFrom([]);
}
