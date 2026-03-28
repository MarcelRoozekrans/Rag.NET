using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Retrieval;

public class TagRetrieverTests
{
    private static IRetriever PassthroughInner()
    {
        var inner = Substitute.For<IRetriever>();
        inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
             .Returns(Result<IReadOnlyList<SearchResult>, RagError>.Success([]));
        return inner;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> MockEmbedder(float[] vector)
    {
        var e = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        e.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
         .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(vector)]));
        return e;
    }

    [Fact]
    public async Task MatchFound_InjectedIntoMetadataFilter()
    {
        var ct     = TestContext.Current.CancellationToken;
        var index  = Substitute.For<ITagIndex>();
        index.Search(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<double>())
             .Returns([(Key: "dept", Value: "finance", Score: 0.95)]);

        RetrievalOptions? captured = null;
        var inner = Substitute.For<IRetriever>();
        inner.RetrieveAsync(Arg.Any<string>(), Arg.Do<RetrievalOptions?>(o => captured = o), ct)
             .Returns(Result<IReadOnlyList<SearchResult>, RagError>.Success([]));

        var sut = new TagRetriever(inner, index, MockEmbedder([0.5f]), new TagRetrievalOptions());
        _ = await sut.RetrieveAsync("budget questions", null, ct);

        Assert.NotNull(captured?.MetadataFilter);
        Assert.Equal("finance", captured!.MetadataFilter!["dept"]);
    }

    [Fact]
    public async Task NoMatches_OptionsPassedUnchanged()
    {
        var ct    = TestContext.Current.CancellationToken;
        var index = Substitute.For<ITagIndex>();
        index.Search(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<double>()).Returns([]);

        RetrievalOptions? captured = null;
        var inner = Substitute.For<IRetriever>();
        inner.RetrieveAsync(Arg.Any<string>(), Arg.Do<RetrievalOptions?>(o => captured = o), ct)
             .Returns(Result<IReadOnlyList<SearchResult>, RagError>.Success([]));

        var sut = new TagRetriever(inner, index, MockEmbedder([0.5f]), new TagRetrievalOptions());
        _ = await sut.RetrieveAsync("query", null, ct);

        Assert.Null(captured?.MetadataFilter);
    }

    [Fact]
    public async Task ExistingCallerFilter_Preserved_NotOverwritten()
    {
        var ct    = TestContext.Current.CancellationToken;
        var index = Substitute.For<ITagIndex>();
        index.Search(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<double>())
             .Returns([(Key: "dept", Value: "finance", Score: 0.95)]);

        RetrievalOptions? captured = null;
        var inner = Substitute.For<IRetriever>();
        inner.RetrieveAsync(Arg.Any<string>(), Arg.Do<RetrievalOptions?>(o => captured = o), ct)
             .Returns(Result<IReadOnlyList<SearchResult>, RagError>.Success([]));

        var options = new RetrievalOptions
        {
            MetadataFilter = new Dictionary<string, string>(StringComparer.Ordinal) { ["dept"] = "legal" }, // caller set this
        };
        var sut = new TagRetriever(inner, index, MockEmbedder([0.5f]), new TagRetrievalOptions());
        _ = await sut.RetrieveAsync("query", options, ct);

        // Caller's value wins — tag match does NOT overwrite
        Assert.Equal("legal", captured!.MetadataFilter!["dept"]);
    }

    [Fact]
    public async Task EmbeddingFailure_OriginalOptionsPassedThrough()
    {
        var ct      = TestContext.Current.CancellationToken;
        var index   = Substitute.For<ITagIndex>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("embedder down"));

        RetrievalOptions? captured = null;
        var inner = Substitute.For<IRetriever>();
        inner.RetrieveAsync(Arg.Any<string>(), Arg.Do<RetrievalOptions?>(o => captured = o), ct)
             .Returns(Result<IReadOnlyList<SearchResult>, RagError>.Success([]));

        var sut = new TagRetriever(inner, index, embedder, new TagRetrievalOptions());
        var result = await sut.RetrieveAsync("query", null, ct);

        Assert.True(result.IsSuccess);
        Assert.Null(captured?.MetadataFilter);
    }

    [Fact]
    public async Task UseTagRetrievalFalse_SkipsEmbeddingAndIndex()
    {
        var ct      = TestContext.Current.CancellationToken;
        var index   = Substitute.For<ITagIndex>();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var inner   = PassthroughInner();

        var sut = new TagRetriever(inner, index, embedder, new TagRetrievalOptions());
        _ = await sut.RetrieveAsync("query", new RetrievalOptions { UseTagRetrieval = false }, ct);

        await embedder.DidNotReceive()
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
        index.DidNotReceive().Search(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<double>());
    }
}
