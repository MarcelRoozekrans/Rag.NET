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

        var summary = actual.First(r => r.Chunk.Metadata.ContainsKey("raptor_level") && r.Chunk.Metadata["raptor_level"] == "1");
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
        Assert.Equal<MetadataValue>("1", actual[0].Chunk.Metadata["raptor_level"]);
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

    [Fact]
    public async Task HandleAsync_FilterMode_MaxLevelOnly_ExcludesHigherLevels()
    {
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Filter, MaxRaptorLevel = 1 };
        var sut = new RaptorRetrievalBehavior(options);
        var results = CreateResults();
        var ctx = CreateContext();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.Equal(2, actual.Count); // leaf (level 0) + level 1
        Assert.DoesNotContain(actual, r => r.Chunk.Metadata.TryGetValue("raptor_level", out var l) && l == "2");
    }

    [Fact]
    public async Task HandleAsync_AllModes_WithEmptyResults_ReturnsEmpty()
    {
        var empty = (IReadOnlyList<SearchResult>)new List<SearchResult>().AsReadOnly();
        var ctx = CreateContext();

        foreach (var mode in Enum.GetValues<RaptorRetrievalMode>())
        {
            var options = new RaptorRetrievalOptions { Mode = mode };
            var sut = new RaptorRetrievalBehavior(options);
            var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(empty));
            Assert.Empty(actual);
        }
    }

    [Fact]
    public async Task HandleAsync_BoostMode_MalformedRaptorLevel_TreatedAsLeaf()
    {
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Boost, SummaryBoostFactor = 2.0 };
        var sut = new RaptorRetrievalBehavior(options);
        var ctx = CreateContext();
        var results = (IReadOnlyList<SearchResult>)new List<SearchResult>
        {
            new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = "bad metadata",
                    DocumentId = new DocumentId("doc"),
                    ChunkIndex = 0,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["raptor_level"] = "not-a-number" },
                },
                Score = 0.5,
            },
        }.AsReadOnly();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.Single(actual);
        Assert.Equal(0.5, actual[0].Score); // no boost — treated as level 0
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
                Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["raptor_level"] = "1", ["raptor_cluster_id"] = "0", ["raptor_child_ids"] = "0" },
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
                Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["raptor_level"] = "2", ["raptor_cluster_id"] = "0", ["raptor_child_ids"] = "1" },
            },
            Score = 0.6,
        },
    ];
}
