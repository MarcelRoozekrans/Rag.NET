using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class MmrRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly MmrRetriever _sut;

    public MmrRetrieverTests()
    {
        _sut = new MmrRetriever(_inner, _embedder);
    }

    private static SearchResult MakeResult(string docId, string text, double score = 0.9) => new()
    {
        Chunk = new TextChunk { Text = text, DocumentId = docId, ChunkIndex = 0 },
        Score = score,
    };

    [Fact]
    public async Task RetrieveAsync_UseMmrFalse_PassesThroughWithoutCallingEmbedder()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("doc-1", "a"), MakeResult("doc-2", "b") };
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns(results);

        var opts = new RetrievalOptions { UseMmr = false };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(2, output.Count);
        await _embedder.DidNotReceiveWithAnyArgs()
            .GenerateAsync(default!, default, ct);
    }

    [Fact]
    public async Task RetrieveAsync_UseMmrTrue_OverFetchesFromInner()
    {
        var ct = TestContext.Current.CancellationToken;
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns([]);
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .Returns(new GeneratedEmbeddings<Embedding<float>>([]));

        var opts = new RetrievalOptions { UseMmr = true, TopK = 3, MmrCandidateCount = 9 };
        await _sut.RetrieveAsync("q", opts, ct);

        // inner must be called with TopK = MmrCandidateCount (9), not the original TopK (3)
        await _inner.Received(1).RetrieveAsync(
            "q",
            Arg.Is<RetrievalOptions?>(o => o!.TopK == 9),
            ct);
    }

    [Fact]
    public async Task RetrieveAsync_UseMmrTrue_DefaultCandidateCount_IsTopKTimesThree()
    {
        var ct = TestContext.Current.CancellationToken;
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns([]);
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .Returns(new GeneratedEmbeddings<Embedding<float>>([]));

        var opts = new RetrievalOptions { UseMmr = true, TopK = 4 };
        await _sut.RetrieveAsync("q", opts, ct);

        await _inner.Received(1).RetrieveAsync(
            "q",
            Arg.Is<RetrievalOptions?>(o => o!.TopK == 12), // 4 * 3
            ct);
    }

    [Fact]
    public async Task RetrieveAsync_NullOptions_DoesNotCallEmbedder()
    {
        // null opts → UseMmr defaults to false → pass-through, no embedding
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("doc-1", "a") };
        _inner.RetrieveAsync("q", null, ct).Returns(results);

        await _sut.RetrieveAsync("q", null, ct);

        await _embedder.DidNotReceiveWithAnyArgs()
            .GenerateAsync(default!, default, ct);
    }

    [Fact]
    public async Task RetrieveAsync_EmbedderFails_ReturnsCandidatesInOriginalOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("doc-1", "a"), MakeResult("doc-2", "b") };
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns(results);
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .ThrowsAsync(new HttpRequestException("API down"));

        var opts = new RetrievalOptions { UseMmr = true, TopK = 2 };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(2, output.Count);
    }

    [Fact]
    public async Task RetrieveAsync_CandidateCountLessThanTopK_ReturnsFewer()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("doc-1", "only one") };
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns(results);

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .Returns(ci =>
            {
                var count = ci.Arg<IEnumerable<string>>().Count();
                var vecs = Enumerable.Range(0, count)
                    .Select(_ => new Embedding<float>(new float[] { 1f, 0f }))
                    .ToList();
                return new GeneratedEmbeddings<Embedding<float>>(vecs);
            });

        // MmrCandidateCount (1) < TopK (5) — should not throw, returns 1 result
        var opts = new RetrievalOptions { UseMmr = true, TopK = 5, MmrCandidateCount = 1 };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Single(output); // can't return more than candidates fetched
    }

    [Fact]
    public async Task RetrieveAsync_CancellationRequested_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("doc-1", "a") };
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns(results);
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .ThrowsAsync(new OperationCanceledException());

        var opts = new RetrievalOptions { UseMmr = true, TopK = 1 };
        await Assert.ThrowsAsync<OperationCanceledException>(() => _sut.RetrieveAsync("q", opts, ct));
    }
}
