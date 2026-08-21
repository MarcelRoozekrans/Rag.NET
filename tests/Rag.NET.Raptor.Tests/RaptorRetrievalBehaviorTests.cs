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

    // ── Over-fetch (phase 6.2.4) ────────────────────────────────────────────

    [Fact]
    public async Task BoostMode_PromotesASummaryIntoTheResultSet_ThatRankedBelowTheCut()
    {
        // The defect: HandleAsync passed ctx to next unmodified and VectorStoreBehavior fetches
        // exactly TopK, so Boost could only reorder within the k already returned. A summary
        // ranked below the cut could never appear, however large its boost — the opposite of
        // what the mode documents.
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Boost, SummaryBoostFactor = 2.0 };
        var sut = new RaptorRetrievalBehavior(options);
        var ctx = CreateContext(topK: 2);

        // Ranks 0-1 are leaves; the summary sits at rank 2, below a TopK of 2. At 2.0x its 0.5
        // becomes 1.0, which beats the second leaf's 0.6.
        var pool = new List<SearchResult>
        {
            Leaf("leaf-a", 0, 0.9),
            Leaf("leaf-b", 1, 0.6),
            Summary("summary", 2, 0.5, level: 1),
        };

        var actual = await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult<IReadOnlyList<SearchResult>>(pool.Take(c.Options.TopK).ToList()));

        Assert.Equal(2, actual.Count);
        Assert.Contains(actual, r => r.Chunk.Metadata.ContainsKey("raptor_level"));
    }

    [Fact]
    public async Task FilterMode_ReturnsTopK_WhenEnoughCandidatesSurviveTheFilter()
    {
        // The defect: Filter dropped summaries out of an already-truncated list, so asking for
        // 2 and having 2 of the 3 fetched be summaries returned 1. A contract violation.
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Filter, MaxRaptorLevel = 0 };
        var sut = new RaptorRetrievalBehavior(options);
        var ctx = CreateContext(topK: 2);

        var pool = new List<SearchResult>
        {
            Summary("summary-a", 0, 0.9, level: 1),
            Summary("summary-b", 1, 0.8, level: 1),
            Leaf("leaf-a", 2, 0.7),
            Leaf("leaf-b", 3, 0.6),
        };

        var actual = await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult<IReadOnlyList<SearchResult>>(pool.Take(c.Options.TopK).ToList()));

        Assert.Equal(2, actual.Count);
        Assert.All(actual, r => Assert.False(r.Chunk.Metadata.ContainsKey("raptor_level")));
    }

    [Fact]
    public async Task BlendMode_DoesNotOverFetch_AndReturnsExactlyTopK()
    {
        // Blend is the shipped default and figures are pinned against it. It must ask for
        // exactly TopK and return exactly what it was given.
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Blend };
        var sut = new RaptorRetrievalBehavior(options);
        var ctx = CreateContext(topK: 2);
        var requestedTopK = -1;

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) =>
        {
            requestedTopK = c.Options.TopK;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>(
                new List<SearchResult> { Leaf("leaf-a", 0, 0.9), Leaf("leaf-b", 1, 0.6) });
        });

        Assert.Equal(2, requestedTopK);
        Assert.Equal(2, actual.Count);
    }

    [Fact]
    public async Task CandidateMultiplierOfOne_ReproducesTheBehaviourBeforeTheOverFetchFix()
    {
        // The control, preserved by configuration rather than by leaving the defect shipped —
        // the same answer 6.2.3 used when it kept PerDocument selectable. At 1.0 the behaviour
        // sees exactly TopK, so a summary below the cut stays invisible however large its boost.
        var options = new RaptorRetrievalOptions
        {
            Mode = RaptorRetrievalMode.Boost,
            SummaryBoostFactor = 2.0,
            CandidateMultiplier = 1.0,
        };
        var sut = new RaptorRetrievalBehavior(options);
        var ctx = CreateContext(topK: 2);

        var pool = new List<SearchResult>
        {
            Leaf("leaf-a", 0, 0.9),
            Leaf("leaf-b", 1, 0.6),
            Summary("summary", 2, 0.5, level: 1),
        };

        var actual = await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult<IReadOnlyList<SearchResult>>(pool.Take(c.Options.TopK).ToList()));

        Assert.Equal(2, actual.Count);
        Assert.DoesNotContain(actual, r => r.Chunk.Metadata.ContainsKey("raptor_level"));
    }

    [Fact]
    public async Task OverFetchDoesNotUnderFill_WhenTheStoreHasFewerCandidatesThanRequested()
    {
        // Over-fetching asks for more than the store may hold. A short store must not become
        // a short result set.
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Boost };
        var sut = new RaptorRetrievalBehavior(options);
        var ctx = CreateContext(topK: 5);

        var pool = new List<SearchResult> { Leaf("leaf-a", 0, 0.9), Leaf("leaf-b", 1, 0.6) };

        var actual = await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult<IReadOnlyList<SearchResult>>(pool.Take(c.Options.TopK).ToList()));

        Assert.Equal(2, actual.Count);
    }

    private static SearchResult Leaf(string text, int index, double score) => new()
    {
        Chunk = new TextChunk { Text = text, DocumentId = new DocumentId("doc"), ChunkIndex = index },
        Score = score,
    };

    private static SearchResult Summary(string text, int index, double score, int level) => new()
    {
        Chunk = new TextChunk
        {
            Text = text,
            DocumentId = new DocumentId("doc"),
            ChunkIndex = index,
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["raptor_level"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        },
        Score = score,
    };

    private static RetrievalContext CreateContext(int topK) => new()
    {
        Query = "test query",
        Options = new RetrievalOptions { TopK = topK },
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
