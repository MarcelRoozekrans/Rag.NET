using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class HydeRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly IHypotheticalDocumentGenerator _generator = Substitute.For<IHypotheticalDocumentGenerator>();

    [Fact]
    public async Task RetrieveAsync_GeneratesHypotheticalDocAndSetsEmbeddingOverride()
    {
        _generator.GenerateAsync("original query", Arg.Any<CancellationToken>())
            .Returns("hypothetical document text");

        RetrievalOptions? capturedOptions = null;
        _inner.RetrieveAsync("original query", Arg.Do<RetrievalOptions?>(o => capturedOptions = o), Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = new HydeRetriever(_inner, _generator);

        await sut.RetrieveAsync("original query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(capturedOptions);
        Assert.Equal("hypothetical document text", capturedOptions!.EmbeddingTextOverride);
        Assert.False(capturedOptions.UseHyde);
    }

    [Fact]
    public async Task RetrieveAsync_PreservesOriginalQueryString()
    {
        _generator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("hypothetical doc");
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = new HydeRetriever(_inner, _generator);

        await sut.RetrieveAsync("original query", cancellationToken: TestContext.Current.CancellationToken);

        await _inner.Received(1).RetrieveAsync("original query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WhenUseHydeFalse_SkipsGeneration()
    {
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = new HydeRetriever(_inner, _generator);

        await sut.RetrieveAsync("query", new RetrievalOptions { UseHyde = false }, TestContext.Current.CancellationToken);

        await _generator.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WhenGeneratorThrows_FallsBackToOriginalQuery()
    {
        _generator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        RetrievalOptions? capturedOptions = null;
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Do<RetrievalOptions?>(o => capturedOptions = o), Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = new HydeRetriever(_inner, _generator);

        await sut.RetrieveAsync("original query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(capturedOptions);
        Assert.Null(capturedOptions!.EmbeddingTextOverride);
        Assert.False(capturedOptions.UseHyde);
    }

    [Fact]
    public async Task RetrieveAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _generator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var sut = new HydeRetriever(_inner, _generator);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.RetrieveAsync("query", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task RetrieveAsync_ReturnsInnerResults()
    {
        var expected = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "result", DocumentId = "doc", ChunkIndex = 0 }, Score = 0.9 }
        };

        _generator.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("hypothetical doc");
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var sut = new HydeRetriever(_inner, _generator);

        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(expected, results);
    }
}
