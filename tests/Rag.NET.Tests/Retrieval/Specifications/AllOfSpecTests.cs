using Rag.NET.Models;
using Rag.NET.Retrieval.Specifications;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Specifications;

public class AllOfSpecTests
{
    private static SearchResult Result(params (string Key, string Value)[] tags)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        foreach (var (key, value) in tags)
            metadata[key] = value;
        return new SearchResult
        {
            Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("d"), ChunkIndex = 0, Metadata = metadata },
            Score = 0.9
        };
    }

    private static AllOfSpec<HasTagSpec> Sut() =>
        new([new HasTagSpec("lang", "en"), new HasTagSpec("topic", "finance")]);

    [Fact]
    public void IsSatisfiedBy_RequiresEveryTag()
    {
        var sut = Sut();

        Assert.True(sut.IsSatisfiedBy(Result(("lang", "en"), ("topic", "finance"))));
        Assert.False(sut.IsSatisfiedBy(Result(("lang", "en"))));
        Assert.False(sut.IsSatisfiedBy(Result(("lang", "en"), ("topic", "legal"))));
    }

    [Fact]
    public void ToExpression_AgreesWithIsSatisfiedBy()
    {
        var sut = Sut();
        var predicate = sut.ToExpression().Compile();

        foreach (var r in new[]
                 {
                     Result(("lang", "en"), ("topic", "finance")),
                     Result(("lang", "en")),
                     Result(("lang", "fr"), ("topic", "finance")),
                 })
        {
            Assert.Equal(sut.IsSatisfiedBy(r), predicate(r));
        }
    }
}
