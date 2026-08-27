using Microsoft.Extensions.AI;
using Rag.NET.Storage;
using NSubstitute;
using Rag.NET.Graph;
using Rag.NET.GraphRag;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

public class LegacyPageRankLocalSearchTests
{
    private readonly IGraphStore _graphStore = Substitute.For<IGraphStore>();

    /// <summary>
    /// The store local search now seeds from (#247).
    /// </summary>
    /// <remarks>
    /// It used to sift entity chunks out of whatever the pipeline returned, which only worked
    /// because those chunks were mixed into the document store — the arrangement that cost −0.21
    /// answer accuracy. With the stores separated, seeds are fetched from here, so the tests put
    /// them here. What a test passes to <c>next</c> is now purely what the caller's retrieval
    /// returned, which is the separation these tests exist under.
    /// </remarks>
    private readonly GraphChunkStore _chunkStore = new(new InMemoryVectorStore());

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    public LegacyPageRankLocalSearchTests()
    {
        _ = _embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                new([new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f })])));

        // Alice is the seed every traversal test walks from. Seeded once here rather than per test:
        // the tests that do not traverse run at PageRankWeight 0, where the walk is skipped outright.
        _chunkStore.Store.StoreAsync(
        [
            new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = "Alice is a person",
                    DocumentId = new DocumentId("doc1"),
                    ChunkIndex = -1,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["graph_type"] = "entity",
                        ["graph_entity_name"] = "Alice",
                    },
                },
                Embedding = new float[] { 0.1f, 0.2f, 0.3f },
            },
        ]).GetAwaiter().GetResult();
    }

    private static RetrievalContext CreateContext() => new()
    {
        Query = "test query",
        Options = new RetrievalOptions(),
    };

    [Fact]
    public async Task HandleAsync_FindsEntitiesAndTraversesNeighbors()
    {
        // PageRankWeight explicit because the walk is opt-in since #239: at the default 0 there is
        // nothing to harvest scores for, so the behaviour skips the graph entirely. A test OF the
        // traversal has to ask for the traversal.
        var options = new LegacyPageRankOptions
        {
            LocalTopEntities = 10,
            LocalSearchDepth = 2,
            PageRankWeight = 0.3,
        };
        var sut = new LegacyPageRankLocalSearch(_graphStore, options, _chunkStore, _embedder);

        var results = CreateEntityResults();
        var ctx = CreateContext();

        _graphStore.GetNeighborsAsync("Alice", 2, Arg.Any<CancellationToken>())
            .Returns([new GraphEntity("Bob", "Person", "Bob desc") { PageRankScore = 0.5 }]);
        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.NotEmpty(actual);
        _ = await _graphStore.Received(1).GetNeighborsAsync("Alice", 2, Arg.Any<CancellationToken>());

        // These two used to be Received(1), which pinned work whose result was discarded: the
        // behaviour awaited both and threw the answers away (#239, point 3). A call-count assertion
        // cannot tell "the code needs this" from "the code issues this and ignores it", so the test
        // held the cost in place and read as coverage.
        //
        // Asserted as DidNotReceive rather than simply dropped. Deleting the assertions would let
        // the calls come back silently, and they are expensive: on the MultiHop-RAG corpus each was
        // a full scan of a 147,021-row table, once per seed entity, roughly 45,000 scans per query
        // pass. If relationships or community reports are ever fed into the result — which is what
        // #239 points 1 and 2 decide — this assertion is the right thing to fail.
        _ = await _graphStore.DidNotReceive().GetRelationshipsAsync("Alice", Arg.Any<CancellationToken>());
        _ = await _graphStore.DidNotReceive().GetCommunitiesForEntityAsync("Alice", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_BlendsPageRankWithSimilarity()
    {
        var options = new LegacyPageRankOptions
        {
            LocalTopEntities = 10,
            LocalSearchDepth = 1,
            PageRankWeight = 0.4,
        };
        var sut = new LegacyPageRankLocalSearch(_graphStore, options, _chunkStore, _embedder);

        var results = CreateEntityResults(); // Alice entity has score 0.9
        var ctx = CreateContext();

        _graphStore.GetNeighborsAsync("Alice", 1, Arg.Any<CancellationToken>())
            .Returns([new GraphEntity("Alice", "Person", "Alice desc") { PageRankScore = 0.8 }]);

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        // Find the Alice entity result
        var aliceResult = actual.First(r =>
            r.Chunk.Metadata.TryGetValue("graph_entity_name", out var n)
            && n == "Alice");

        // Expected: (1 - 0.4) * 0.9 + 0.4 * 0.8 = 0.54 + 0.32 = 0.86
        Assert.Equal(0.86, aliceResult.Score, precision: 5);
    }

    [Fact]
    public async Task HandleAsync_NoEntityResults_ReturnsStandardResults()
    {
        // PageRankWeight explicit at 0: the shared constructor seeds an "Alice" entity chunk into
        // _chunkStore for every test, so despite this test's name the entity search here does
        // find a result. On the shipped type this test passed anyway because the default was 0,
        // which skips the walk regardless of what the entity search finds; LegacyPageRankOptions
        // defaults to 0.3, so that has to be requested explicitly here too.
        var options = new LegacyPageRankOptions { PageRankWeight = 0 };
        var sut = new LegacyPageRankLocalSearch(_graphStore, options, _chunkStore, _embedder);
        var ctx = CreateContext();

        var results = (IReadOnlyList<SearchResult>)new List<SearchResult>
        {
            new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = "plain text",
                    DocumentId = new DocumentId("doc1"),
                    ChunkIndex = 0,
                },
                Score = 0.7,
            },
        }.AsReadOnly();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.Single(actual);
        Assert.Equal(0.7, actual[0].Score);
        await _graphStore.DidNotReceive().GetNeighborsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_RespectsLocalSearchDepth()
    {
        // As above: non-zero weight, or there is no walk whose depth could be respected.
        var options = new LegacyPageRankOptions
        {
            LocalSearchDepth = 3,
            LocalTopEntities = 10,
            PageRankWeight = 0.3,
        };
        var sut = new LegacyPageRankLocalSearch(_graphStore, options, _chunkStore, _embedder);

        var results = CreateEntityResults();
        var ctx = CreateContext();

        _graphStore.GetNeighborsAsync("Alice", 3, Arg.Any<CancellationToken>())
            .Returns([]);

        await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        await _graphStore.Received(1).GetNeighborsAsync("Alice", 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_EntitiesWithZeroPageRank_BlendingStable()
    {
        var options = new LegacyPageRankOptions
        {
            LocalTopEntities = 10,
            LocalSearchDepth = 1,
            PageRankWeight = 0.4,
        };
        var sut = new LegacyPageRankLocalSearch(_graphStore, options, _chunkStore, _embedder);

        var results = CreateEntityResults(); // Alice entity has score 0.9
        var ctx = CreateContext();

        // Entity with PageRankScore = 0.0
        _graphStore.GetNeighborsAsync("Alice", 1, Arg.Any<CancellationToken>())
            .Returns([new GraphEntity("Alice", "Person", "Alice desc") { PageRankScore = 0.0 }]);

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        // Find the Alice entity result
        var aliceResult = actual.First(r =>
            r.Chunk.Metadata.TryGetValue("graph_entity_name", out var n)
            && n == "Alice");

        // Expected: (1 - 0.4) * 0.9 + 0.4 * 0.0 = 0.54 + 0.0 = 0.54
        Assert.Equal(0.54, aliceResult.Score, precision: 5);
    }

    [Fact]
    public async Task HandleAsync_ChunksFromDifferentDocumentsSharingChunkIndex_BothSurvive()
    {
        var options = new LegacyPageRankOptions { LocalTopEntities = 10, LocalSearchDepth = 1 };
        var sut = new LegacyPageRankLocalSearch(_graphStore, options, _chunkStore, _embedder);
        StubEmptyGraph();

        var results = (IReadOnlyList<SearchResult>)new List<SearchResult>
        {
            CreateAliceEntityResult(),
            CreateChunkResult("docA", 3, 0.8),
            CreateChunkResult("docB", 3, 0.4),
        }.AsReadOnly();

        var actual = await sut.HandleAsync(
            CreateContext(), CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.Equal(3, actual.Count);
        Assert.Contains(actual, r => string.Equals(r.Chunk.DocumentId.Value, "docA", StringComparison.Ordinal) && r.Chunk.ChunkIndex == 3);
        Assert.Contains(actual, r => string.Equals(r.Chunk.DocumentId.Value, "docB", StringComparison.Ordinal) && r.Chunk.ChunkIndex == 3);
    }

    [Fact]
    public async Task HandleAsync_DuplicateChunkWithinOneDocument_CollapsesToHighestScore()
    {
        var options = new LegacyPageRankOptions { LocalTopEntities = 10, LocalSearchDepth = 1 };
        var sut = new LegacyPageRankLocalSearch(_graphStore, options, _chunkStore, _embedder);
        StubEmptyGraph();

        var results = (IReadOnlyList<SearchResult>)new List<SearchResult>
        {
            CreateAliceEntityResult(),
            CreateChunkResult("docA", 3, 0.4),
            CreateChunkResult("docA", 3, 0.8),
        }.AsReadOnly();

        var actual = await sut.HandleAsync(
            CreateContext(), CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.Equal(2, actual.Count);
        var deduplicated = Assert.Single(actual, r => string.Equals(r.Chunk.DocumentId.Value, "docA", StringComparison.Ordinal));
        Assert.Equal(0.8, deduplicated.Score, precision: 5);
    }

    private void StubEmptyGraph()
    {
        _graphStore.GetNeighborsAsync("Alice", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private static SearchResult CreateAliceEntityResult() => new()
    {
        Chunk = new TextChunk
        {
            Text = "Alice is a person",
            DocumentId = new DocumentId("docEntities"),
            ChunkIndex = -1,
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["graph_type"] = "entity",
                ["graph_entity_name"] = "Alice",
            },
        },
        Score = 0.9,
    };

    private static SearchResult CreateChunkResult(string documentId, int chunkIndex, double score) => new()
    {
        Chunk = new TextChunk
        {
            Text = $"{documentId} chunk {chunkIndex}",
            DocumentId = new DocumentId(documentId),
            ChunkIndex = chunkIndex,
        },
        Score = score,
    };

    private static IReadOnlyList<SearchResult> CreateEntityResults() =>
        new List<SearchResult>
        {
            new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = "Alice is a person",
                    DocumentId = new DocumentId("doc1"),
                    ChunkIndex = 0,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["graph_type"] = "entity",
                        ["graph_entity_name"] = "Alice",
                    },
                },
                Score = 0.9,
            },
            new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = "some other chunk",
                    DocumentId = new DocumentId("doc2"),
                    ChunkIndex = 1,
                },
                Score = 0.5,
            },
        }.AsReadOnly();

    /// <remarks>
    /// <para>
    /// The default is <c>PageRankWeight = 0</c> (#239), at which the blend is the identity — so every
    /// PageRank score the walk would collect is read by nothing. The walk must therefore not happen.
    /// </para>
    /// <para>
    /// This is the same class of defect as #239 point 3, which removed two graph calls whose results
    /// were discarded: defaulting the weight to 0 without this check would have reintroduced exactly
    /// that waste one level up, and on the default path rather than an opt-in one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AtTheDefaultWeight_NoGraphWalkHappensAtAll()
    {
        var sut = new LegacyPageRankLocalSearch(_graphStore, new LegacyPageRankOptions { PageRankWeight = 0 }, _chunkStore, _embedder);
        var results = CreateEntityResults();

        var actual = await sut.HandleAsync(
            CreateContext(), CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.NotEmpty(actual);
        _ = await _graphStore.DidNotReceive()
            .GetNeighborsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <remarks>
    /// The other direction: a caller who opts in must still get the walk, or the skip above would be
    /// a silent disabling of the feature rather than a default change.
    /// </remarks>
    [Fact]
    public async Task AtANonZeroWeight_TheWalkStillHappens()
    {
        var options = new LegacyPageRankOptions { PageRankWeight = 0.4, LocalSearchDepth = 1 };
        var sut = new LegacyPageRankLocalSearch(_graphStore, options, _chunkStore, _embedder);
        _graphStore.GetNeighborsAsync("Alice", 1, Arg.Any<CancellationToken>())
            .Returns([new GraphEntity("Alice", "Person", "Alice desc") { PageRankScore = 0.8 }]);

        _ = await sut.HandleAsync(
            CreateContext(), CancellationToken.None, (c, ct) => ValueTask.FromResult(CreateEntityResults()));

        _ = await _graphStore.Received(1)
            .GetNeighborsAsync("Alice", 1, Arg.Any<CancellationToken>());
    }

    /// <remarks>
    /// Deduplication must survive the skip. <c>BlendAndDeduplicate</c> does two jobs, and returning
    /// the input list at <c>w = 0</c> would have been the obvious shortcut and would have restored
    /// #231's duplicate-candidate defect on the default path.
    /// </remarks>
    [Fact]
    public async Task AtTheDefaultWeight_DuplicatesAreStillCollapsed()
    {
        var sut = new LegacyPageRankLocalSearch(_graphStore, new LegacyPageRankOptions { PageRankWeight = 0 }, _chunkStore, _embedder);

        var results = (IReadOnlyList<SearchResult>)new List<SearchResult>
        {
            CreateAliceEntityResult(),
            CreateChunkResult("docA", 3, 0.4),
            CreateChunkResult("docA", 3, 0.8),
        }.AsReadOnly();

        var actual = await sut.HandleAsync(
            CreateContext(), CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        var docA = actual.Where(r => string.Equals(r.Chunk.DocumentId.Value, "docA", StringComparison.Ordinal)).ToList();
        _ = Assert.Single(docA);
        Assert.Equal(0.8, docA[0].Score, precision: 5);
    }
}
