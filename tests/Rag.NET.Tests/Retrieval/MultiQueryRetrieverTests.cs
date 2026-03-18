using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class MultiQueryRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly IQueryExpander _expander = Substitute.For<IQueryExpander>();
    private readonly MultiQueryOptions _options = new() { VariantCount = 2 };
    private readonly MultiQueryRetriever _sut;

    public MultiQueryRetrieverTests()
    {
        _sut = new MultiQueryRetriever(_inner, _expander, _options);
    }

    private static SearchResult MakeResult(string docId, int chunkIndex, double score) =>
        new()
        {
            Chunk = new TextChunk { Text = $"{docId}-{chunkIndex}", DocumentId = docId, ChunkIndex = chunkIndex },
            Score = score
        };

    [Fact]
    public async Task RetrieveAsync_ExpandsQueryAndMergesResults()
    {
        var ct = TestContext.Current.CancellationToken;

        _expander.ExpandAsync("original", 2, ct)
            .Returns(new List<string> { "variant1", "variant2" });

        _inner.RetrieveAsync("original", Arg.Any<RetrievalOptions?>(), ct)
            .Returns([MakeResult("doc-1", 0, 0.9)]);
        _inner.RetrieveAsync("variant1", Arg.Any<RetrievalOptions?>(), ct)
            .Returns([MakeResult("doc-2", 0, 0.8)]);
        _inner.RetrieveAsync("variant2", Arg.Any<RetrievalOptions?>(), ct)
            .Returns([MakeResult("doc-3", 0, 0.7)]);

        var opts = new RetrievalOptions { UseMultiQuery = true, TopK = 10 };
        var results = await _sut.RetrieveAsync("original", opts, ct);

        Assert.Equal(3, results.Count);
        Assert.Equal("doc-1", results[0].Chunk.DocumentId);
        Assert.Equal("doc-2", results[1].Chunk.DocumentId);
        Assert.Equal("doc-3", results[2].Chunk.DocumentId);
    }

    [Fact]
    public async Task RetrieveAsync_DeduplicatesByDocIdAndChunkIndex_KeepingHighestScore()
    {
        var ct = TestContext.Current.CancellationToken;

        _expander.ExpandAsync("q", 2, ct)
            .Returns(new List<string> { "v1" });

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns([MakeResult("doc-1", 0, 0.7)]);
        _inner.RetrieveAsync("v1", Arg.Any<RetrievalOptions?>(), ct)
            .Returns([MakeResult("doc-1", 0, 0.9)]);

        var opts = new RetrievalOptions { UseMultiQuery = true, TopK = 10 };
        var results = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Single(results);
        Assert.Equal(0.9, results[0].Score);
    }

    [Fact]
    public async Task RetrieveAsync_UseMultiQueryFalse_SkipsExpansion()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(expected);

        var opts = new RetrievalOptions { UseMultiQuery = false };
        var results = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Single(results);
        await _expander.DidNotReceiveWithAnyArgs().ExpandAsync(default!, default, ct);
    }

    [Fact]
    public async Task RetrieveAsync_ExpanderThrows_FallsBackToSingleQuery()
    {
        var ct = TestContext.Current.CancellationToken;

        _expander.ExpandAsync("q", 2, ct)
            .ThrowsAsync(new InvalidOperationException("LLM error"));

        var expected = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(expected);

        var opts = new RetrievalOptions { UseMultiQuery = true, TopK = 10 };
        var results = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Single(results);
        Assert.Equal("doc-1", results[0].Chunk.DocumentId);
    }

    [Fact]
    public async Task RetrieveAsync_ExpanderThrowsOperationCanceled_Propagates()
    {
        var ct = TestContext.Current.CancellationToken;

        _expander.ExpandAsync("q", 2, ct)
            .ThrowsAsync(new OperationCanceledException());

        var opts = new RetrievalOptions { UseMultiQuery = true };
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.RetrieveAsync("q", opts, ct));
    }

    [Fact]
    public async Task RetrieveAsync_VariantQueryThrows_ReturnsResultsFromSuccessfulQueries()
    {
        var ct = TestContext.Current.CancellationToken;

        _expander.ExpandAsync("q", 2, ct)
            .Returns(new List<string> { "variant1", "variant2" });

        // Original query succeeds
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns([MakeResult("doc-1", 0, 0.9)]);
        // variant1 throws
        _inner.RetrieveAsync("variant1", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(Task.FromException<IReadOnlyList<SearchResult>>(new InvalidOperationException("retriever failed")));
        // variant2 succeeds
        _inner.RetrieveAsync("variant2", Arg.Any<RetrievalOptions?>(), ct)
            .Returns([MakeResult("doc-2", 0, 0.7)]);

        var opts = new RetrievalOptions { UseMultiQuery = true, TopK = 10 };
        var results = await _sut.RetrieveAsync("q", opts, ct);

        // Both successful query results must be in the output
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => string.Equals(r.Chunk.DocumentId, "doc-1", StringComparison.Ordinal));
        Assert.Contains(results, r => string.Equals(r.Chunk.DocumentId, "doc-2", StringComparison.Ordinal));
    }
}
