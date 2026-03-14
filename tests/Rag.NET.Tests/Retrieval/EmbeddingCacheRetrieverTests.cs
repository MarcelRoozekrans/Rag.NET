using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class EmbeddingCacheRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly HybridCache _cache;
    private readonly CachingOptions _options = new();

    public EmbeddingCacheRetrieverTests()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        _cache = sp.GetRequiredService<HybridCache>();
    }

    [Fact]
    public async Task RetrieveAsync_CallsInnerOnFirstCall()
    {
        var expected = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "hit", DocumentId = "d1", ChunkIndex = 0 }, Score = 0.9 }
        };
        _inner.RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var sut = new EmbeddingCacheRetriever(_inner, _cache, _options);
        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected.Count, results.Count);
        await _inner.Received(1).RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_SecondCallUsesCache()
    {
        _inner.RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = new EmbeddingCacheRetriever(_inner, _cache, _options);
        await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);
        await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        await _inner.Received(1).RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_WhenOptedOut_SkipsCache()
    {
        _inner.RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = new EmbeddingCacheRetriever(_inner, _cache, _options);
        var opts = new RetrievalOptions { UseCacheEmbedding = false };
        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);
        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        await _inner.Received(2).RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_DifferentQueriesGetDifferentCacheEntries()
    {
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = new EmbeddingCacheRetriever(_inner, _cache, _options);
        await sut.RetrieveAsync("query1", cancellationToken: TestContext.Current.CancellationToken);
        await sut.RetrieveAsync("query2", cancellationToken: TestContext.Current.CancellationToken);

        await _inner.Received(2).RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsync_UsesEmbeddingTextOverrideForCacheKey()
    {
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = new EmbeddingCacheRetriever(_inner, _cache, _options);
        var opts = new RetrievalOptions { EmbeddingTextOverride = "hypothetical doc" };
        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);
        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        await _inner.Received(1).RetrieveAsync("query", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }
}
