using Microsoft.Extensions.Logging;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Retrieval;

public class TimeWeightedRetrieverTests
{
    private static TextChunk MakeChunk(string? createdAt = null)
    {
        var chunk = new TextChunk
        {
            Text       = "content",
            DocumentId = new DocumentId("doc1"),
            ChunkIndex = 0,
        };
        if (createdAt is not null)
            chunk.Metadata["created_at"] = createdAt;
        return chunk;
    }

    private static IRetriever MockInner(IReadOnlyList<SearchResult> results)
    {
        var inner = Substitute.For<IRetriever>();
        inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
             .Returns(Result<IReadOnlyList<SearchResult>, RagError>.Success(results));
        return inner;
    }

    [Fact]
    public async Task OldDocument_ScoreReducedByDecay()
    {
        var ct        = TestContext.Current.CancellationToken;
        var createdAt = DateTime.UtcNow.AddHours(-100); // 100 hours ago → e^(-0.01×100) ≈ 0.368
        var chunk     = MakeChunk(createdAt.ToString("O"));
        var inner     = MockInner([new SearchResult { Chunk = chunk, Score = 1.0 }]);

        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions { DecayRate = 0.01 });
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        // e^(-1) ≈ 0.368 — accept small timing jitter
        Assert.InRange(result.Value[0].Score, 0.35, 0.39);
    }

    [Fact]
    public async Task TwoResults_ResortedByDecayedScore()
    {
        var ct     = TestContext.Current.CancellationToken;
        // Use chunks with different DocumentIds to distinguish them
        var freshChunk = new TextChunk
        {
            Text       = "fresh",
            DocumentId = new DocumentId("fresh"),
            ChunkIndex = 0,
        };
        freshChunk.Metadata["created_at"] = DateTime.UtcNow.AddHours(-1).ToString("O");

        var oldChunk = new TextChunk
        {
            Text       = "old",
            DocumentId = new DocumentId("old"),
            ChunkIndex = 0,
        };
        oldChunk.Metadata["created_at"] = DateTime.UtcNow.AddHours(-100).ToString("O");

        // Old document has higher raw similarity but ages out
        var inner = MockInner([
            new SearchResult { Chunk = oldChunk,   Score = 0.95 },
            new SearchResult { Chunk = freshChunk, Score = 0.80 },
        ]);

        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions { DecayRate = 0.01 });
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        // Fresh document (score 0.80 × ~0.99) must outrank old document (0.95 × ~0.368)
        Assert.Equal("fresh", result.Value[0].Chunk.DocumentId.Value);
        Assert.Equal("old",   result.Value[1].Chunk.DocumentId.Value);
    }

    [Fact]
    public async Task NoTimestamp_ScoreUnchanged()
    {
        var ct    = TestContext.Current.CancellationToken;
        var chunk = MakeChunk(); // no created_at metadata
        var inner = MockInner([new SearchResult { Chunk = chunk, Score = 0.75 }]);

        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.Equal(0.75, result.Value[0].Score);
    }

    [Fact]
    public async Task InvalidTimestamp_TreatedAsNoTimestamp()
    {
        var ct    = TestContext.Current.CancellationToken;
        var chunk = MakeChunk("not-a-date");
        var inner = MockInner([new SearchResult { Chunk = chunk, Score = 0.75 }]);

        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.Equal(0.75, result.Value[0].Score);
    }

    [Fact]
    public async Task UseTimeWeightingFalse_InnerCalledWithOriginalOptions_ScoresUnchanged()
    {
        var ct    = TestContext.Current.CancellationToken;
        var chunk = MakeChunk(DateTime.UtcNow.AddHours(-100).ToString("O"));

        RetrievalOptions? captured = null;
        var inner = Substitute.For<IRetriever>();
        inner.RetrieveAsync(Arg.Any<string>(), Arg.Do<RetrievalOptions?>(o => captured = o), ct)
             .Returns(Result<IReadOnlyList<SearchResult>, RagError>.Success(
                 [new SearchResult { Chunk = chunk, Score = 0.9 }]));

        var opts   = new RetrievalOptions { UseTimeWeighting = false };
        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions { DecayRate = 0.01 });
        var result = await sut.RetrieveAsync("q", opts, ct);

        Assert.Equal(0.9, result.Value[0].Score);  // score unchanged
        Assert.False(captured?.UseTimeWeighting);   // original options passed through
    }

    [Fact]
    public async Task FallbackMetadataKey_UsedWhenCreatedAtAbsent()
    {
        var ct    = TestContext.Current.CancellationToken;
        var chunk = new TextChunk
        {
            Text       = "content",
            DocumentId = new DocumentId("doc1"),
            ChunkIndex = 0,
        };
        chunk.Metadata["published_at"] = DateTime.UtcNow.AddHours(-100).ToString("O");
        // no "created_at" key

        var inner  = MockInner([new SearchResult { Chunk = chunk, Score = 1.0 }]);
        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions
        {
            DecayRate            = 0.01,
            FallbackMetadataKeys = ["published_at"],
        });
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.InRange(result.Value[0].Score, 0.35, 0.39); // decay applied via fallback
    }

    [Fact]
    public async Task FallbackMetadataKeys_FirstParseableWins()
    {
        var ct    = TestContext.Current.CancellationToken;
        var chunk = new TextChunk
        {
            Text       = "content",
            DocumentId = new DocumentId("doc1"),
            ChunkIndex = 0,
        };
        chunk.Metadata["key_a"] = "not-a-date";                                 // unparseable
        chunk.Metadata["key_b"] = DateTime.UtcNow.AddHours(-100).ToString("O"); // parseable

        var inner  = MockInner([new SearchResult { Chunk = chunk, Score = 1.0 }]);
        var sut    = new TimeWeightedRetriever(inner, new TimeWeightedOptions
        {
            DecayRate            = 0.01,
            FallbackMetadataKeys = ["key_a", "key_b"],
        });
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.InRange(result.Value[0].Score, 0.35, 0.39); // key_b used
    }
}
