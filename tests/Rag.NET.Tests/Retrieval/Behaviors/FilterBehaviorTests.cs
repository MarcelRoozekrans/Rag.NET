using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Rag.NET.Retrieval.Specifications;
using ZeroAlloc.Specification;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class FilterBehaviorTests
{
    private static SearchResult MakeResult(string docId, double score) =>
        new() { Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId(docId), ChunkIndex = 0 }, Score = score };

    private static RetrievalContext MakeCtx(ISpecification<SearchResult>? filter) =>
        new() { Query = "q", Options = new RetrievalOptions { Filter = filter } };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        NextReturning(IReadOnlyList<SearchResult> results) =>
        (_, _) => ValueTask.FromResult(results);

    [Fact]
    public async Task Filter_WhenNull_ReturnsAllResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult> { MakeResult("d1", 0.9), MakeResult("d2", 0.5) };
        var sut = new FilterBehavior();

        var output = await sut.HandleAsync(MakeCtx(null), ct, NextReturning(results));

        Assert.Same(results, output);
    }

    [Fact]
    public async Task Filter_WithMinScore_RemovesBelowThreshold()
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new List<SearchResult>
        {
            MakeResult("d1", 0.9),
            MakeResult("d2", 0.5),
            MakeResult("d3", 0.85),
        };
        var sut = new FilterBehavior();

        var output = await sut.HandleAsync(MakeCtx(new MinScoreSpec(0.8)), ct, NextReturning(results));

        Assert.Equal(2, output.Count);
        Assert.All(output, r => Assert.True(r.Score >= 0.8));
    }

    [Fact]
    public async Task Filter_AndSpec_AppliesBothConditions()
    {
        var ct = TestContext.Current.CancellationToken;
        var r1 = new SearchResult
        {
            Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("d1"), ChunkIndex = 0,
                Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["lang"] = "en" } },
            Score = 0.9
        };
        var r2 = new SearchResult
        {
            Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("d2"), ChunkIndex = 0,
                Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["lang"] = "fr" } },
            Score = 0.9
        };
        var results = new List<SearchResult> { r1, r2 };
        var sut = new FilterBehavior();
        var spec = new MinScoreSpec(0.8).And(new HasTagSpec("lang", "en"));

        var output = await sut.HandleAsync(MakeCtx(spec), ct, NextReturning(results));

        Assert.Single(output);
        Assert.Equal(new DocumentId("d1"), output[0].Chunk.DocumentId);
    }
}
