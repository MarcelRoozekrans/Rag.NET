using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Raptor.Tests;

public class RaptorRetrievalBehaviorTests
{
    [Fact]
    public async Task HandleAsync_BlendMode_PassesThroughUnmodified()
    {
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Blend };
        var sut = new RaptorRetrievalBehavior(options);
        var results = CreateResults();
        var ctx = CreateContext();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.Equal(results, actual);
    }

    [Fact]
    public async Task HandleAsync_BoostMode_MultipliesSummaryScores()
    {
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Boost, SummaryBoostFactor = 2.0 };
        var sut = new RaptorRetrievalBehavior(options);
        var results = CreateResults();
        var ctx = CreateContext();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        var leaf = actual.First(r => !r.Chunk.Metadata.ContainsKey("raptor_level"));
        Assert.Equal(0.8, leaf.Score);

        var summary = actual.First(r => r.Chunk.Metadata.ContainsKey("raptor_level") && string.Equals(r.Chunk.Metadata["raptor_level"], "1", StringComparison.Ordinal));
        Assert.Equal(1.4, summary.Score, precision: 5);
    }

    [Fact]
    public async Task HandleAsync_FilterMode_RestrictsToLevelRange()
    {
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Filter, MinRaptorLevel = 1, MaxRaptorLevel = 1 };
        var sut = new RaptorRetrievalBehavior(options);
        var results = CreateResults();
        var ctx = CreateContext();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.Single(actual);
        Assert.Equal("1", actual[0].Chunk.Metadata["raptor_level"]);
    }

    [Fact]
    public async Task HandleAsync_FilterMode_MinLevelOnly_IncludesHigherLevels()
    {
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Filter, MinRaptorLevel = 1 };
        var sut = new RaptorRetrievalBehavior(options);
        var results = CreateResults();
        var ctx = CreateContext();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.Equal(2, actual.Count);
        Assert.All(actual, r => Assert.True(r.Chunk.Metadata.ContainsKey("raptor_level")));
    }

    [Fact]
    public async Task HandleAsync_BoostMode_ResultsAreSortedByScore()
    {
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Boost, SummaryBoostFactor = 3.0 };
        var sut = new RaptorRetrievalBehavior(options);
        var results = CreateResults();
        var ctx = CreateContext();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        for (int i = 1; i < actual.Count; i++)
            Assert.True(actual[i - 1].Score >= actual[i].Score, "Results should be sorted descending by score");
    }

    private static RetrievalContext CreateContext() => new()
    {
        Query = "test query",
        Options = new RetrievalOptions(),
    };

    private static IReadOnlyList<SearchResult> CreateResults() =>
    [
        new SearchResult
        {
            Chunk = new TextChunk { Text = "leaf content", DocumentId = new DocumentId("doc"), ChunkIndex = 0 },
            Score = 0.8,
        },
        new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = "summary level 1",
                DocumentId = new DocumentId("doc"),
                ChunkIndex = 1,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["raptor_level"] = "1", ["raptor_cluster_id"] = "0", ["raptor_child_ids"] = "0" },
            },
            Score = 0.7,
        },
        new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = "summary level 2",
                DocumentId = new DocumentId("doc"),
                ChunkIndex = 2,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["raptor_level"] = "2", ["raptor_cluster_id"] = "0", ["raptor_child_ids"] = "1" },
            },
            Score = 0.6,
        },
    ];
}
