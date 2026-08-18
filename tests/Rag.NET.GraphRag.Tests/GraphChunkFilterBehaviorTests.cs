using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// Covers the #247 filter: synthetic chunks leave the results, everything else survives.
/// </summary>
/// <remarks>
/// <para>
/// GraphRAG indexes entity, relationship and community-report chunks into the same vector store as
/// the article chunks — 303,503 beside 17,648 on MultiHop-RAG — and dense retrieval treated them as
/// peers of the text. That cost −0.043 nDCG@10 and −0.21 answer accuracy, and filtering them out of
/// the results recovered all of it.
/// </para>
/// <para>
/// Two of these tests guard properties that would fail silently: that <c>global_answer</c> survives
/// (a tag-blind filter deletes global search's entire output) and that the caller still gets
/// <c>TopK</c> when there is material to fill it.
/// </para>
/// </remarks>
public sealed class GraphChunkFilterBehaviorTests
{
    [Theory]
    [InlineData("entity")]
    [InlineData("relationship")]
    [InlineData("community_report")]
    public async Task IndexedSyntheticChunksAreRemoved(string graphType)
    {
        var results = new[] { Article("a", 0.9), Synthetic(graphType, 0.8), Article("b", 0.7) };
        var sut = new GraphChunkFilterBehavior(new GraphRagRetrievalOptions());

        var actual = await sut.HandleAsync(Context(topK: 3), CancellationToken.None,
            (c, ct) => ValueTask.FromResult<IReadOnlyList<SearchResult>>(results));

        Assert.Equal(2, actual.Count);
        Assert.DoesNotContain(actual, r =>
            r.Chunk.Metadata.ContainsKey("graph_type"));
    }

    /// <remarks>
    /// <b>The one that would fail silently.</b> <c>GraphGlobalSearchBehavior</c> tags its synthesised
    /// answer with <c>graph_type</c> like everything else, so a filter that went by the presence of
    /// the tag would delete global search's entire output and leave a caller wondering why global
    /// search returned candidates but no answer.
    /// </remarks>
    [Fact]
    public async Task TheGlobalSearchAnswerIsNotFiltered()
    {
        var results = new[] { Synthetic("global_answer", 1.0), Synthetic("entity", 0.8), Article("a", 0.7) };
        var sut = new GraphChunkFilterBehavior(new GraphRagRetrievalOptions());

        var actual = await sut.HandleAsync(Context(topK: 3), CancellationToken.None,
            (c, ct) => ValueTask.FromResult<IReadOnlyList<SearchResult>>(results));

        Assert.Equal(2, actual.Count);
        Assert.Equal("global_answer", actual[0].Chunk.Metadata["graph_type"].ToString());
    }

    /// <remarks>
    /// The over-fetch is what makes filtering free rather than a recall cut: without it, removing
    /// synthetic chunks would hand back fewer results than the caller asked for.
    /// </remarks>
    [Fact]
    public async Task ItOverFetchesSoTheCallerStillGetsTopK()
    {
        var requested = 0;
        var options = new GraphRagRetrievalOptions { GraphChunkOverFetchFactor = 20 };
        var sut = new GraphChunkFilterBehavior(options);

        var actual = await sut.HandleAsync(Context(topK: 5), CancellationToken.None, (c, ct) =>
        {
            requested = c.Options.TopK;

            // A store where synthetic chunks outnumber articles, which is the real shape: 17:1 on
            // MultiHop-RAG. Nine of every ten here are synthetic.
            var many = new List<SearchResult>();
            for (var i = 0; i < requested; i++)
            {
                many.Add(i % 10 == 0 ? Article($"a{i}", 1.0 - (i / 1000.0)) : Synthetic("entity", 1.0 - (i / 1000.0)));
            }

            return ValueTask.FromResult<IReadOnlyList<SearchResult>>(many);
        });

        Assert.Equal(100, requested);      // 5 x 20
        Assert.Equal(5, actual.Count);     // still filled
        Assert.All(actual, r => Assert.DoesNotContain("graph_type", r.Chunk.Metadata.Keys, StringComparer.Ordinal));
    }

    /// <remarks>
    /// Off means untouched — including the over-fetch, which would otherwise change what the inner
    /// pipeline is asked for and quietly alter results for someone who opted out.
    /// </remarks>
    [Fact]
    public async Task WhenDisabledNothingIsFilteredAndNothingIsOverFetched()
    {
        var requested = 0;
        var results = new[] { Synthetic("entity", 0.9), Article("a", 0.8) };
        var sut = new GraphChunkFilterBehavior(
            new GraphRagRetrievalOptions { FilterGraphChunksFromResults = false });

        var actual = await sut.HandleAsync(Context(topK: 7), CancellationToken.None, (c, ct) =>
        {
            requested = c.Options.TopK;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>(results);
        });

        Assert.Equal(7, requested);
        Assert.Equal(2, actual.Count);
    }

    [Fact]
    public void FilteringIsOnByDefault()
    {
        // Pinned because it is the decision, not an implementation detail: leaving the synthetic
        // chunks in was measured at -0.21 answer accuracy.
        Assert.True(new GraphRagRetrievalOptions().FilterGraphChunksFromResults);
        Assert.Equal(20, new GraphRagRetrievalOptions().GraphChunkOverFetchFactor);
    }

    private static RetrievalContext Context(int topK) => new()
    {
        Query = "q",
        Options = new RetrievalOptions { TopK = topK },
    };

    private static SearchResult Article(string id, double score) => new()
    {
        Chunk = new TextChunk { Text = $"article {id}", DocumentId = new DocumentId(id), ChunkIndex = 0 },
        Score = score,
    };

    private static SearchResult Synthetic(string graphType, double score) => new()
    {
        Chunk = new TextChunk
        {
            Text = $"{graphType} text",
            DocumentId = new DocumentId("g"),
            ChunkIndex = -1,
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["graph_type"] = graphType,
            },
        },
        Score = score,
    };
}
