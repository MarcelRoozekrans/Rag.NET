using Rag.NET.Models;
using Rag.NET.Retrieval.Specifications;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Specifications;

public class SpecificationTests
{
    private static SearchResult MakeResult(string docId, double score, string? tagKey = null, string? tagValue = null)
    {
        var chunk = new TextChunk { Text = "t", DocumentId = new DocumentId(docId), ChunkIndex = 0 };
        if (tagKey is not null)
            chunk.Metadata[tagKey] = tagValue!;
        return new SearchResult { Chunk = chunk, Score = score };
    }

    [Fact]
    public void MinScoreSpec_PassesAboveThreshold()
    {
        var spec = new MinScoreSpec(0.8);
        Assert.True(spec.IsSatisfiedBy(MakeResult("d", 0.9)));
        Assert.False(spec.IsSatisfiedBy(MakeResult("d", 0.7)));
    }

    [Fact]
    public void HasTagSpec_MatchesExactKeyValue()
    {
        var spec = new HasTagSpec("lang", "en");
        Assert.True(spec.IsSatisfiedBy(MakeResult("d", 1.0, "lang", "en")));
        Assert.False(spec.IsSatisfiedBy(MakeResult("d", 1.0, "lang", "fr")));
        Assert.False(spec.IsSatisfiedBy(MakeResult("d", 1.0)));
    }

    [Fact]
    public void DocumentIdSpec_MatchesById()
    {
        var spec = new DocumentIdSpec(new DocumentId("doc-1"));
        Assert.True(spec.IsSatisfiedBy(MakeResult("doc-1", 1.0)));
        Assert.False(spec.IsSatisfiedBy(MakeResult("doc-2", 1.0)));
    }

    [Fact]
    public void AndSpec_RequiresBoth()
    {
        var spec = new MinScoreSpec(0.8).And(new HasTagSpec("lang", "en"));
        Assert.True(spec.IsSatisfiedBy(MakeResult("d", 0.9, "lang", "en")));
        Assert.False(spec.IsSatisfiedBy(MakeResult("d", 0.9, "lang", "fr"))); // tag fails
        Assert.False(spec.IsSatisfiedBy(MakeResult("d", 0.5, "lang", "en"))); // score fails
    }
}
