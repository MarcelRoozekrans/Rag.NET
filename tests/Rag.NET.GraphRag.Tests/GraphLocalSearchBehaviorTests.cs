using NSubstitute;
using Rag.NET.Graph;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

public class GraphLocalSearchBehaviorTests
{
    private readonly IGraphStore _graphStore = Substitute.For<IGraphStore>();

    private static RetrievalContext CreateContext() => new()
    {
        Query = "test query",
        Options = new RetrievalOptions(),
    };

    [Fact]
    public async Task HandleAsync_FindsEntitiesAndTraversesNeighbors()
    {
        var options = new GraphRagRetrievalOptions { LocalTopEntities = 10, LocalSearchDepth = 2 };
        var sut = new GraphLocalSearchBehavior(_graphStore, options);

        var results = CreateEntityResults();
        var ctx = CreateContext();

        _graphStore.GetNeighborsAsync("Alice", 2, Arg.Any<CancellationToken>())
            .Returns([new GraphEntity("Bob", "Person", "Bob desc") { PageRankScore = 0.5 }]);
        _graphStore.GetRelationshipsAsync("Alice", Arg.Any<CancellationToken>())
            .Returns([new GraphRelationship("Alice", "Bob", "knows")]);
        _graphStore.GetCommunitiesForEntityAsync("Alice", Arg.Any<CancellationToken>())
            .Returns([new Community(1, 0, ["Alice", "Bob"], "Community about Alice and Bob")]);

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.NotEmpty(actual);
        await _graphStore.Received(1).GetNeighborsAsync("Alice", 2, Arg.Any<CancellationToken>());
        await _graphStore.Received(1).GetRelationshipsAsync("Alice", Arg.Any<CancellationToken>());
        await _graphStore.Received(1).GetCommunitiesForEntityAsync("Alice", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_BlendsPageRankWithSimilarity()
    {
        var options = new GraphRagRetrievalOptions
        {
            LocalTopEntities = 10,
            LocalSearchDepth = 1,
            PageRankWeight = 0.4,
        };
        var sut = new GraphLocalSearchBehavior(_graphStore, options);

        var results = CreateEntityResults(); // Alice entity has score 0.9
        var ctx = CreateContext();

        _graphStore.GetNeighborsAsync("Alice", 1, Arg.Any<CancellationToken>())
            .Returns([new GraphEntity("Alice", "Person", "Alice desc") { PageRankScore = 0.8 }]);
        _graphStore.GetRelationshipsAsync("Alice", Arg.Any<CancellationToken>())
            .Returns([]);
        _graphStore.GetCommunitiesForEntityAsync("Alice", Arg.Any<CancellationToken>())
            .Returns([]);

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
        var options = new GraphRagRetrievalOptions();
        var sut = new GraphLocalSearchBehavior(_graphStore, options);
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
        var options = new GraphRagRetrievalOptions { LocalSearchDepth = 3, LocalTopEntities = 10 };
        var sut = new GraphLocalSearchBehavior(_graphStore, options);

        var results = CreateEntityResults();
        var ctx = CreateContext();

        _graphStore.GetNeighborsAsync("Alice", 3, Arg.Any<CancellationToken>())
            .Returns([]);
        _graphStore.GetRelationshipsAsync("Alice", Arg.Any<CancellationToken>())
            .Returns([]);
        _graphStore.GetCommunitiesForEntityAsync("Alice", Arg.Any<CancellationToken>())
            .Returns([]);

        await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        await _graphStore.Received(1).GetNeighborsAsync("Alice", 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_EntitiesWithZeroPageRank_BlendingStable()
    {
        var options = new GraphRagRetrievalOptions
        {
            LocalTopEntities = 10,
            LocalSearchDepth = 1,
            PageRankWeight = 0.4,
        };
        var sut = new GraphLocalSearchBehavior(_graphStore, options);

        var results = CreateEntityResults(); // Alice entity has score 0.9
        var ctx = CreateContext();

        // Entity with PageRankScore = 0.0
        _graphStore.GetNeighborsAsync("Alice", 1, Arg.Any<CancellationToken>())
            .Returns([new GraphEntity("Alice", "Person", "Alice desc") { PageRankScore = 0.0 }]);
        _graphStore.GetRelationshipsAsync("Alice", Arg.Any<CancellationToken>())
            .Returns([]);
        _graphStore.GetCommunitiesForEntityAsync("Alice", Arg.Any<CancellationToken>())
            .Returns([]);

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        // Find the Alice entity result
        var aliceResult = actual.First(r =>
            r.Chunk.Metadata.TryGetValue("graph_entity_name", out var n)
            && n == "Alice");

        // Expected: (1 - 0.4) * 0.9 + 0.4 * 0.0 = 0.54 + 0.0 = 0.54
        Assert.Equal(0.54, aliceResult.Score, precision: 5);
    }

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
}
