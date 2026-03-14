using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class RedundancyFilterRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly RedundancyFilterRetriever _sut;

    public RedundancyFilterRetrieverTests()
    {
        _sut = new RedundancyFilterRetriever(_inner, _embedder);
    }

    private static SearchResult MakeResult(string docId, int chunkIndex, double score, string text = "text") =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = docId, ChunkIndex = chunkIndex },
            Score = score
        };

    [Fact]
    public async Task RetrieveAsync_UseRedundancyFilterTrue_FiltersDuplicateResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9, "hello"),
            MakeResult("doc-2", 0, 0.8, "hello again"),
        };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(results);

        var identicalVector = new float[] { 1.0f, 0.0f, 0.0f };
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
            [
                new Embedding<float>(identicalVector),
                new Embedding<float>(identicalVector),
            ]));

        var opts = new RetrievalOptions { UseRedundancyFilter = true };
        var filtered = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Single(filtered);
        Assert.Equal("doc-1", filtered[0].Chunk.DocumentId);
    }

    [Fact]
    public async Task RetrieveAsync_UseRedundancyFilterFalse_PassesThroughWithoutCallingEmbedder()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
        };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(results);

        var opts = new RetrievalOptions { UseRedundancyFilter = false };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(2, output.Count);
        await _embedder.DidNotReceiveWithAnyArgs()
            .GenerateAsync(default!, default, ct);
    }

    [Fact]
    public async Task RetrieveAsync_EmptyResults_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(new List<SearchResult>());

        var opts = new RetrievalOptions { UseRedundancyFilter = true };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Empty(output);
    }

    [Fact]
    public async Task RetrieveAsync_SingleResult_ReturnsSameResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.9, "only one") };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(results);

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
            [
                new Embedding<float>(new float[] { 1.0f, 0.0f }),
            ]));

        var opts = new RetrievalOptions { UseRedundancyFilter = true };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Single(output);
        Assert.Equal("doc-1", output[0].Chunk.DocumentId);
    }

    [Fact]
    public async Task RetrieveAsync_DissimilarVectors_KeepsAll()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9, "first"),
            MakeResult("doc-2", 0, 0.8, "second"),
        };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(results);

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .Returns(new GeneratedEmbeddings<Embedding<float>>(
            [
                new Embedding<float>(new float[] { 1.0f, 0.0f, 0.0f }),
                new Embedding<float>(new float[] { 0.0f, 1.0f, 0.0f }),
            ]));

        var opts = new RetrievalOptions { UseRedundancyFilter = true };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(2, output.Count);
    }

    [Fact]
    public async Task RetrieveAsync_EmbedderFails_ReturnsUnfilteredResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9),
            MakeResult("doc-2", 0, 0.8),
        };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(results);

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .ThrowsAsync(new HttpRequestException("API down"));

        var opts = new RetrievalOptions { UseRedundancyFilter = true };
        var output = await _sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(2, output.Count);
    }

    [Fact]
    public async Task RetrieveAsync_CancellationRequested_Throws()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var ct = cts.Token;
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };

        _inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(results);

        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), ct)
            .ThrowsAsync(new OperationCanceledException());

        var opts = new RetrievalOptions { UseRedundancyFilter = true };
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.RetrieveAsync("q", opts, ct));
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
