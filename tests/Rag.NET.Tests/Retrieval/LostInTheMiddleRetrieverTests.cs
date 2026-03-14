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

        // LostInTheMiddleReorderer puts even-indexed (0,2) at left, odd-indexed (1,3) at right
        // Expected: doc-1, doc-3, doc-4, doc-2
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
}
