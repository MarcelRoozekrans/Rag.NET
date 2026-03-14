using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class RerankingRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly IReranker _reranker = Substitute.For<IReranker>();
    private readonly RerankingRetriever _sut;

    public RerankingRetrieverTests()
    {
        _sut = new RerankingRetriever(_inner, _reranker);
    }

    private static SearchResult MakeResult(string docId, int chunkIndex, double score) =>
        new()
        {
            Chunk = new TextChunk { Text = $"{docId}-{chunkIndex}", DocumentId = docId, ChunkIndex = chunkIndex },
            Score = score
        };

    [Fact]
    public async Task RetrieveAsync_OverFetchesAndReranks_ReturnsTopKByRerankerScore()
    {
        var ct = TestContext.Current.CancellationToken;
        var candidates = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
            MakeResult("doc-3", 0, 0.7),
        };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(candidates);

        _reranker.RerankAsync("q", candidates, ct)
            .Returns(new List<RerankResult>
            {
                new() { SearchResult = candidates[2], RelevanceScore = 0.95 },
                new() { SearchResult = candidates[0], RelevanceScore = 0.85 },
                new() { SearchResult = candidates[1], RelevanceScore = 0.5 },
            });

        var opts = new RetrievalOptions { UseReranking = true, TopK = 2 };
        var results = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(2, results.Count);
        Assert.Equal("doc-3", results[0].Chunk.DocumentId);
        Assert.Equal("doc-1", results[1].Chunk.DocumentId);
    }

    [Fact]
    public async Task RetrieveAsync_UseRerankingFalse_SkipsReranking()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(expected);

        var opts = new RetrievalOptions { UseReranking = false };
        var results = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Single(results);
        await _reranker.DidNotReceiveWithAnyArgs().RerankAsync(default!, default!, ct);
    }

    [Fact]
    public async Task RetrieveAsync_DefaultCandidateCount_IsTopKTimes3()
    {
        var ct = TestContext.Current.CancellationToken;

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(new List<SearchResult>());

        _reranker.RerankAsync("q", Arg.Any<IReadOnlyList<SearchResult>>(), ct)
            .Returns(new List<RerankResult>());

        var opts = new RetrievalOptions { UseReranking = true, TopK = 5 };
        await _sut.RetrieveAsync("q", opts, ct);

        await _inner.Received(1).RetrieveAsync("q",
            Arg.Is<RetrievalOptions?>(o => o != null && o.TopK == 15 && !o.UseReranking), ct);
    }

    [Fact]
    public async Task RetrieveAsync_ExplicitCandidateCount_OverridesDefault()
    {
        var ct = TestContext.Current.CancellationToken;

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(new List<SearchResult>());

        _reranker.RerankAsync("q", Arg.Any<IReadOnlyList<SearchResult>>(), ct)
            .Returns(new List<RerankResult>());

        var opts = new RetrievalOptions { UseReranking = true, TopK = 5, CandidateCount = 20 };
        await _sut.RetrieveAsync("q", opts, ct);

        await _inner.Received(1).RetrieveAsync("q",
            Arg.Is<RetrievalOptions?>(o => o != null && o.TopK == 20), ct);
    }

    [Fact]
    public async Task RetrieveAsync_RerankerThrows_FallsBackToUnrankedResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var candidates = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(candidates);

        _reranker.RerankAsync("q", candidates, ct)
            .ThrowsAsync(new InvalidOperationException("reranker down"));

        var opts = new RetrievalOptions { UseReranking = true, TopK = 5 };
        var results = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Single(results);
        Assert.Equal("doc-1", results[0].Chunk.DocumentId);
    }

    [Fact]
    public async Task RetrieveAsync_RerankerThrowsOperationCanceled_Propagates()
    {
        var ct = TestContext.Current.CancellationToken;

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(new List<SearchResult> { MakeResult("doc-1", 0, 0.9) });

        _reranker.RerankAsync("q", Arg.Any<IReadOnlyList<SearchResult>>(), ct)
            .ThrowsAsync(new OperationCanceledException());

        var opts = new RetrievalOptions { UseReranking = true, TopK = 5 };
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.RetrieveAsync("q", opts, ct));
    }

    [Fact]
    public async Task RetrieveAsync_PassesUseRerankingFalseToInner_PreventsInfiniteRecursion()
    {
        var ct = TestContext.Current.CancellationToken;

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(new List<SearchResult>());

        _reranker.RerankAsync("q", Arg.Any<IReadOnlyList<SearchResult>>(), ct)
            .Returns(new List<RerankResult>());

        var opts = new RetrievalOptions { UseReranking = true, TopK = 5 };
        await _sut.RetrieveAsync("q", opts, ct);

        await _inner.Received(1).RetrieveAsync("q",
            Arg.Is<RetrievalOptions?>(o => o != null && !o.UseReranking), ct);
    }
}
