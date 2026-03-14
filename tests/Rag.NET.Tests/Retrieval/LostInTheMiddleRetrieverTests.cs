using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class LostInTheMiddleRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly LostInTheMiddleRetriever _sut;

    public LostInTheMiddleRetrieverTests()
    {
        _sut = new LostInTheMiddleRetriever(_inner);
    }

    private static SearchResult MakeResult(string docId, int chunkIndex, double score) =>
        new()
        {
            Chunk = new TextChunk { Text = $"{docId}-{chunkIndex}", DocumentId = docId, ChunkIndex = chunkIndex },
            Score = score
        };

    [Fact]
    public async Task RetrieveAsync_UseLostInTheMiddleReorderingTrue_ReordersResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
            MakeResult("doc-3", 0, 0.7),
            MakeResult("doc-4", 0, 0.6),
        };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(results);

        var opts = new RetrievalOptions { UseLostInTheMiddleReordering = true };
        var reordered = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(4, reordered.Count);
        Assert.Equal("doc-1", reordered[0].Chunk.DocumentId);
        Assert.Equal("doc-3", reordered[1].Chunk.DocumentId);
        Assert.Equal("doc-4", reordered[2].Chunk.DocumentId);
        Assert.Equal("doc-2", reordered[3].Chunk.DocumentId);
    }

    [Fact]
    public async Task RetrieveAsync_UseLostInTheMiddleReorderingFalse_ReturnsSameListReference()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
        };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(results);

        var opts = new RetrievalOptions { UseLostInTheMiddleReordering = false };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Same(results, output);
    }

    [Fact]
    public async Task RetrieveAsync_EmptyResults_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(new List<SearchResult>());

        var opts = new RetrievalOptions { UseLostInTheMiddleReordering = true };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Empty(output);
    }

    [Fact]
    public async Task RetrieveAsync_SingleResult_ReturnsSingleElement()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(results);

        var opts = new RetrievalOptions { UseLostInTheMiddleReordering = true };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Single(output);
        Assert.Equal("doc-1", output[0].Chunk.DocumentId);
    }

    [Fact]
    public async Task RetrieveAsync_TwoResults_ReturnsBothPreserved()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
        };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(results);

        var opts = new RetrievalOptions { UseLostInTheMiddleReordering = true };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(2, output.Count);
    }

    [Fact]
    public async Task RetrieveAsync_OddNumberOfResults_ReordersCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
            MakeResult("doc-3", 0, 0.7),
            MakeResult("doc-4", 0, 0.6),
            MakeResult("doc-5", 0, 0.5),
        };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(results);

        var opts = new RetrievalOptions { UseLostInTheMiddleReordering = true };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(5, output.Count);
        // Even-indexed inputs (0,2,4) go left, odd-indexed (1,3) go right
        Assert.Equal("doc-1", output[0].Chunk.DocumentId);
        Assert.Equal("doc-3", output[1].Chunk.DocumentId);
        Assert.Equal("doc-5", output[2].Chunk.DocumentId);
        Assert.Equal("doc-4", output[3].Chunk.DocumentId);
        Assert.Equal("doc-2", output[4].Chunk.DocumentId);
    }

    [Fact]
    public async Task RetrieveAsync_NullOptions_UsesDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };

        _inner.RetrieveAsync("q", null, ct)
            .Returns(results);

        var output = await _sut.RetrieveAsync("q", null, ct);

        Assert.Single(output);
    }
}
